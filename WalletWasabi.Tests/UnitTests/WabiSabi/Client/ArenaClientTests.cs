using Microsoft.Extensions.Caching.Memory;
using Moq;
using NBitcoin;
using NBitcoin.RPC;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Backend.Controllers.WabiSabi;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Backend.Statistics;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

public class ArenaClientTests
{
	[Fact]
	public async Task FullCoinjoinAsyncTestAsync()
	{
		var config = new WabiSabiConfig { MaxInputCountByRound = 1 };
		var round = WabiSabiFactory.CreateRound(config);
		round.MaxVsizeAllocationPerAlice = 255;
		using var key = new Key();
		var outpoint = BitcoinFactory.CreateOutPoint();
		var mockRpc = new Mock<IRPCClient>();
		mockRpc.Setup(rpc => rpc.GetTxOutAsync(outpoint.Hash, (int)outpoint.N, true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new NBitcoin.RPC.GetTxOutResponse
			{
				IsCoinBase = false,
				Confirmations = 200,
				TxOut = new TxOut(Money.Coins(1m), key.PubKey.WitHash.GetAddress(Network.Main)),
			});
		mockRpc.Setup(rpc => rpc.EstimateSmartFeeAsync(It.IsAny<int>(), It.IsAny<EstimateSmartFeeMode>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new EstimateSmartFeeResponse
			{
				Blocks = 1000,
				FeeRate = new FeeRate(10m)
			});
		mockRpc.Setup(rpc => rpc.GetMempoolInfoAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MemPoolInfo
			{
				MinRelayTxFee = 1
			});
		mockRpc.Setup(rpc => rpc.PrepareBatch()).Returns(mockRpc.Object);
		mockRpc.Setup(rpc => rpc.SendBatchAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		mockRpc.Setup(rpc => rpc.GetRawTransactionAsync(It.IsAny<uint256>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(BitcoinFactory.CreateTransaction());

		using Arena arena = await ArenaBuilder.From(config).With(mockRpc).CreateAndStartAsync(round);
		await arena.TriggerAndWaitRoundAsync(TimeSpan.FromMinutes(1));

		using var memoryCache = new MemoryCache(new MemoryCacheOptions());
		var idempotencyRequestCache = new IdempotencyRequestCache(memoryCache);

		using CoinJoinFeeRateStatStore coinJoinFeeRateStatStore = new(config, arena.Rpc);
		var wabiSabiApi = new WabiSabiController(idempotencyRequestCache, arena, coinJoinFeeRateStatStore);

		var insecureRandom = new InsecureRandom();
		var roundState = RoundState.FromRound(round);
		var aliceArenaClient = new ArenaClient(
			roundState.CreateAmountCredentialClient(insecureRandom),
			roundState.CreateVsizeCredentialClient(insecureRandom),
			wabiSabiApi);
		var ownershipProof = WabiSabiFactory.CreateOwnershipProof(key, round.Id);

		var (inputRegistrationResponse, _) = await aliceArenaClient.RegisterInputAsync(round.Id, outpoint, ownershipProof, CancellationToken.None);
		var aliceId = inputRegistrationResponse.Value;

		var inputVsize = Constants.P2wpkhInputVirtualSize;
		var amountsToRequest = new[]
		{
			Money.Coins(.75m) - round.FeeRate.GetFee(inputVsize) - round.CoordinationFeeRate.GetFee(Money.Coins(1m)),
			Money.Coins(.25m),
		}.Select(x => x.Satoshi).ToArray();

		using var destinationKey1 = new Key();
		using var destinationKey2 = new Key();
		var p2wpkhScriptSize = (long)destinationKey1.PubKey.WitHash.ScriptPubKey.EstimateOutputVsize();

		var vsizesToRequest = new[] { roundState.MaxVsizeAllocationPerAlice - (inputVsize + 2 * p2wpkhScriptSize), 2 * p2wpkhScriptSize };

		// Phase: Input Registration
		Assert.Equal(Phase.InputRegistration, round.Phase);

		var connectionConfirmationResponse1 = await aliceArenaClient.ConfirmConnectionAsync(
			round.Id,
			aliceId,
			amountsToRequest,
			vsizesToRequest,
			inputRegistrationResponse.IssuedAmountCredentials,
			inputRegistrationResponse.IssuedVsizeCredentials,
			CancellationToken.None);

		await arena.TriggerAndWaitRoundAsync(TimeSpan.FromMinutes(1));
		Assert.Equal(Phase.ConnectionConfirmation, round.Phase);

		// Phase: Connection Confirmation
		var connectionConfirmationResponse2 = await aliceArenaClient.ConfirmConnectionAsync(
			round.Id,
			aliceId,
			amountsToRequest,
			vsizesToRequest,
			connectionConfirmationResponse1.IssuedAmountCredentials,
			connectionConfirmationResponse1.IssuedVsizeCredentials,
			CancellationToken.None);

		await arena.TriggerAndWaitRoundAsync(TimeSpan.FromSeconds(1));

		// Phase: Output Registration
		Assert.Equal(Phase.OutputRegistration, round.Phase);

		var bobArenaClient = new ArenaClient(
			roundState.CreateAmountCredentialClient(insecureRandom),
			roundState.CreateVsizeCredentialClient(insecureRandom),
			wabiSabiApi);

		var reissuanceResponse = await bobArenaClient.ReissueCredentialAsync(
			round.Id,
			amountsToRequest,
			Enumerable.Repeat(p2wpkhScriptSize, 2),
			connectionConfirmationResponse2.IssuedAmountCredentials.Take(ProtocolConstants.CredentialNumber),
			connectionConfirmationResponse2.IssuedVsizeCredentials.Skip(1).Take(ProtocolConstants.CredentialNumber), // first amount is the leftover value
			CancellationToken.None);

		Credential amountCred1 = reissuanceResponse.IssuedAmountCredentials.ElementAt(0);
		Credential amountCred2 = reissuanceResponse.IssuedAmountCredentials.ElementAt(1);
		Credential zeroAmountCred1 = reissuanceResponse.IssuedAmountCredentials.ElementAt(2);
		Credential zeroAmountCred2 = reissuanceResponse.IssuedAmountCredentials.ElementAt(3);

		Credential vsizeCred1 = reissuanceResponse.IssuedVsizeCredentials.ElementAt(0);
		Credential vsizeCred2 = reissuanceResponse.IssuedVsizeCredentials.ElementAt(1);
		Credential zeroVsizeCred1 = reissuanceResponse.IssuedVsizeCredentials.ElementAt(2);
		Credential zeroVsizeCred2 = reissuanceResponse.IssuedVsizeCredentials.ElementAt(3);

		await bobArenaClient.RegisterOutputAsync(
			round.Id,
			destinationKey1.PubKey.WitHash.ScriptPubKey,
			new[] { amountCred1, zeroAmountCred1 },
			new[] { vsizeCred1, zeroVsizeCred1 },
			CancellationToken.None);

		await bobArenaClient.RegisterOutputAsync(
			round.Id,
			destinationKey2.PubKey.WitHash.ScriptPubKey,
			new[] { amountCred2, zeroAmountCred2 },
			new[] { vsizeCred2, zeroVsizeCred2 },
			CancellationToken.None);

		await aliceArenaClient.ReadyToSignAsync(round.Id, aliceId, CancellationToken.None);

		await arena.TriggerAndWaitRoundAsync(TimeSpan.FromMinutes(1));
		Assert.Equal(Phase.TransactionSigning, round.Phase);

		var tx = round.Assert<SigningState>().CreateTransaction();
		Assert.Single(tx.Inputs);
		Assert.Equal(2 + 1, tx.Outputs.Count); // +1 because it pays coordination fees
	}

	[Fact]
	public async Task RemoveInputAsyncTestAsync()
	{
		var config = new WabiSabiConfig();
		var round = WabiSabiFactory.CreateRound(config);
		round.SetPhase(Phase.ConnectionConfirmation);
		var fundingTx = BitcoinFactory.CreateSmartTransaction(ownOutputCount: 1);
		var coin = fundingTx.WalletOutputs.First().Coin;
		var alice = new Alice(coin, new OwnershipProof(), round, Guid.NewGuid(), false);
		round.Alices.Add(alice);

		using Arena arena = await ArenaBuilder.From(config).CreateAndStartAsync(round);

		using var memoryCache = new MemoryCache(new MemoryCacheOptions());
		var idempotencyRequestCache = new IdempotencyRequestCache(memoryCache);
		using CoinJoinFeeRateStatStore coinJoinFeeRateStatStore = new(config, arena.Rpc);
		var wabiSabiApi = new WabiSabiController(idempotencyRequestCache, arena, coinJoinFeeRateStatStore);

		var apiClient = new ArenaClient(null!, null!, wabiSabiApi);

		round.SetPhase(Phase.InputRegistration);

		await apiClient.RemoveInputAsync(round.Id, alice.Id, CancellationToken.None);
		Assert.Empty(round.Alices);
	}

	[Fact]
	public async Task SignTransactionAsync()
	{
		WabiSabiConfig config = new();
		Round round = WabiSabiFactory.CreateRound(config);
		var password = "satoshi";

		var km = ServiceFactory.CreateKeyManager(password);
		var keyChain = new KeyChain(km, new Kitchen(password));
		var destinationProvider = new InternalDestinationProvider(km);

		var coins = destinationProvider.GetNextDestinations(2)
			.Select(dest => (
				Coin: new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Money.Coins(1.0m), dest)),
				OwnershipProof: keyChain.GetOwnershipProof(dest, new CoinJoinInputCommitmentData("test", uint256.One))))
			.ToArray();

		Alice alice1 = WabiSabiFactory.CreateAlice(coins[0].Coin, coins[0].OwnershipProof, round: round);
		round.Alices.Add(alice1);

		Alice alice2 = WabiSabiFactory.CreateAlice(coins[1].Coin, coins[1].OwnershipProof, round: round);
		round.Alices.Add(alice2);

		using Arena arena = await ArenaBuilder.From(config).CreateAndStartAsync(round);

		var mockRpc = new Mock<IRPCClient>();
		using var memoryCache = new MemoryCache(new MemoryCacheOptions());
		var idempotencyRequestCache = new IdempotencyRequestCache(memoryCache);

		using CoinJoinFeeRateStatStore coinJoinFeeRateStatStore = new(config, arena.Rpc);
		var wabiSabiApi = new WabiSabiController(idempotencyRequestCache, arena, coinJoinFeeRateStatStore);

		InsecureRandom rnd = InsecureRandom.Instance;
		var amountClient = new WabiSabiClient(round.AmountCredentialIssuerParameters, rnd, 4300000000000L);
		var vsizeClient = new WabiSabiClient(round.VsizeCredentialIssuerParameters, rnd, 2000L);
		var apiClient = new ArenaClient(amountClient, vsizeClient, wabiSabiApi);

		round.SetPhase(Phase.TransactionSigning);

		var emptyState = round.Assert<ConstructionState>();

		// We can't use ``emptyState.Finalize()` because this is not a valid transaction so we fake it
		var finalizedEmptyState = new SigningState(emptyState.Parameters, emptyState.Events);

		// No inputs in the coinjoin.
		await Assert.ThrowsAsync<ArgumentException>(async () =>
				await apiClient.SignTransactionAsync(round.Id, alice1.Coin, coins[0].OwnershipProof, keyChain, finalizedEmptyState.CreateUnsignedTransaction(), CancellationToken.None));

		var oneInput = emptyState.AddInput(alice1.Coin).Finalize();
		round.CoinjoinState = oneInput;

		// Trying to sign coins those are not in the coinjoin.
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
				await apiClient.SignTransactionAsync(round.Id, alice2.Coin, coins[1].OwnershipProof, keyChain, oneInput.CreateUnsignedTransaction(), CancellationToken.None));

		var twoInputs = emptyState.AddInput(alice1.Coin).AddInput(alice2.Coin).Finalize();
		round.CoinjoinState = twoInputs;

		Assert.False(round.Assert<SigningState>().IsFullySigned);
		var unsigned = round.Assert<SigningState>().CreateUnsignedTransaction();

		await apiClient.SignTransactionAsync(round.Id, alice1.Coin, coins[0].OwnershipProof, keyChain, unsigned, CancellationToken.None);
		Assert.True(round.Assert<SigningState>().IsInputSigned(alice1.Coin.Outpoint));
		Assert.False(round.Assert<SigningState>().IsInputSigned(alice2.Coin.Outpoint));

		Assert.False(round.Assert<SigningState>().IsFullySigned);

		await apiClient.SignTransactionAsync(round.Id, alice2.Coin, coins[1].OwnershipProof, keyChain, unsigned, CancellationToken.None);
		Assert.True(round.Assert<SigningState>().IsInputSigned(alice2.Coin.Outpoint));

		Assert.True(round.Assert<SigningState>().IsFullySigned);
	}
}
