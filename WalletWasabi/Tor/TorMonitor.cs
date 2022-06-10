using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Bases;
using WalletWasabi.Logging;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Exceptions;
using WalletWasabi.Tor.Control.Messages;
using WalletWasabi.Tor.Control.Messages.CircuitStatus;
using WalletWasabi.Tor.Control.Messages.Events;
using WalletWasabi.Tor.Control.Messages.Events.OrEvents;
using WalletWasabi.Tor.Control.Messages.Events.StatusEvents;
using WalletWasabi.Tor.Control.Utils;
using WalletWasabi.Tor.Http;
using WalletWasabi.Tor.Socks5.Exceptions;
using WalletWasabi.Tor.Socks5.Models.Fields.OctetFields;
using WalletWasabi.Tor.Socks5.Pool;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.Tor;

/// <summary>Monitors Tor process bootstrap and reachability of Wasabi Backend.</summary>
public class TorMonitor : PeriodicRunner
{
	public static readonly TimeSpan CheckIfRunningAfterTorMisbehavedFor = TimeSpan.FromSeconds(7);

	public TorMonitor(TimeSpan period, Uri fallbackBackendUri, TorProcessManager torProcessManager, HttpClientFactory backendHttpClientFactory) : base(period)
	{
		FallbackBackendUri = fallbackBackendUri;
		TorProcessManager = torProcessManager;
		TorHttpPool = backendHttpClientFactory.TorHttpPool!;
		HttpClient = backendHttpClientFactory.NewTorHttpClient(Mode.DefaultCircuit);
	}

	private CancellationTokenSource LoopCts { get; } = new();

	public static bool RequestFallbackAddressUsage { get; private set; }
	private Uri FallbackBackendUri { get; }
	private TorHttpClient HttpClient { get; }
	private TorProcessManager TorProcessManager { get; }
	private TorHttpPool TorHttpPool { get; }

	private Task? BootstrapTask { get; set; }

	/// <inheritdoc/>
	public override Task StartAsync(CancellationToken cancellationToken)
	{
		BootstrapTask = StartBootstrapMonitorAsync(cancellationToken);

		return base.StartAsync(cancellationToken);
	}

	private async Task StartBootstrapMonitorAsync(CancellationToken appShutdownToken)
	{
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appShutdownToken, LoopCts.Token);

		while (!linkedCts.IsCancellationRequested)
		{
			try
			{
				(CancellationToken cancellationToken, TorControlClient client) = await TorProcessManager.WaitForNextAttemptAsync(linkedCts.Token).ConfigureAwait(false);

				Logger.LogInfo("Starting Tor bootstrap monitor…");

				List<string> eventNames = new()
				{
					StatusEvent.EventNameStatusGeneral,
					StatusEvent.EventNameStatusClient,
					StatusEvent.EventNameStatusServer,
					CircEvent.EventName,
					OrConnEvent.EventName,
					NetworkLivenessEvent.EventName,
				};
				await client.SubscribeEventsAsync(eventNames, cancellationToken).ConfigureAwait(false);

				bool circuitEstablished = false;

				await foreach (TorControlReply reply in client.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
				{
					IAsyncEvent asyncEvent;

					try
					{
						asyncEvent = AsyncEventParser.Parse(reply);
					}
					catch (TorControlReplyParseException e)
					{
						Logger.LogError($"Exception thrown when parsing event: '{reply}'", e);
						continue;
					}

					if (asyncEvent is BootstrapStatusEvent bootstrapEvent)
					{
						BootstrapStatusEvent.Phases.TryGetValue(bootstrapEvent.Progress, out string? bootstrapInfo);
						Logger.LogInfo($"Bootstrap progress: {bootstrapEvent.Progress}/100 ({bootstrapInfo ?? "N/A"})");
					}
					else if (asyncEvent is StatusEvent statusEvent)
					{
						if (!circuitEstablished && statusEvent.Action == StatusEvent.ActionCircuitEstablished)
						{
							Logger.LogInfo("Tor circuit was established.");
							circuitEstablished = true;
						}
					}
					else if (asyncEvent is CircEvent circEvent)
					{
						CircuitInfo info = circEvent.CircuitInfo;

						if (!circuitEstablished && info.CircStatus is CircStatus.BUILT or CircStatus.EXTENDED or CircStatus.GUARD_WAIT)
						{
							Logger.LogInfo("Tor circuit was established.");
							circuitEstablished = true;
						}

						if (info.CircStatus == CircStatus.CLOSED && info.UserName is not null)
						{
							Logger.LogTrace($"Tor circuit #{info.CircuitID} ('{info.UserName}') was closed.");
							await TorHttpPool.ReportCircuitClosedAsync(info.UserName, linkedCts.Token).ConfigureAwait(false);
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				if (linkedCts.IsCancellationRequested)
				{
					Logger.LogDebug("Tor Monitor is stopping.");
				}
				else
				{
					Logger.LogDebug("Tor Monitor is re-initializing.");
				}
			}
			catch (Exception e)
			{
				Logger.LogError(e);
				break;
			}
		}
	}

	/// <inheritdoc/>
	protected override async Task ActionAsync(CancellationToken token)
	{
		if (TorHttpPool.TorDoesntWorkSince is not null) // If Tor misbehaves.
		{
			TimeSpan torMisbehavedFor = DateTimeOffset.UtcNow - TorHttpPool.TorDoesntWorkSince ?? TimeSpan.Zero;

			if (torMisbehavedFor > CheckIfRunningAfterTorMisbehavedFor)
			{
				if (TorHttpPool.LatestTorException is TorConnectCommandFailedException torEx)
				{
					if (torEx.RepField == RepField.HostUnreachable)
					{
						Logger.LogInfo("Tor cannot access remote host. Test fallback URI.");
						
						try
						{
							using HttpRequestMessage request = new(HttpMethod.Get, FallbackBackendUri);
							using HttpResponseMessage _ = await HttpClient.SendAsync(request, token).ConfigureAwait(false);
						}
						catch (HttpRequestException)
						{
							// Ignore HTTP exceptions as we want to verify that HOST is really unreachable.
						}

						// Check if it changed in the meantime...
						if (TorHttpPool.LatestTorException is TorConnectCommandFailedException torEx2 && torEx2.RepField == RepField.HostUnreachable)
						{
							// Fallback here...
							RequestFallbackAddressUsage = true;
						}
					}
				}
				else
				{
					bool isRunning = await HttpClient.IsTorRunningAsync().ConfigureAwait(false);

					if (isRunning)
					{
						Logger.LogInfo("Tor is running. Waiting for a confirmation that HTTP requests can pass through.");
					}
				}
			}
		}
	}

	/// <inheritdoc/>
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		LoopCts.Cancel();

		if (BootstrapTask is not null)
		{
			Logger.LogDebug("Wait until Tor bootstrap monitor finishes.");
			await BootstrapTask.ConfigureAwait(false);
		}

		await base.StopAsync(cancellationToken).ConfigureAwait(false);
	}
}
