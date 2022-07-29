using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using DynamicData;
using ReactiveUI;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.ViewModels.Dialogs;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Fluent.Views.Wallets.Advanced.WalletCoins.Columns;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Advanced.WalletCoins;

[NavigationMetaData(Title = "Wallet Coins (UTXOs)")]
public partial class WalletCoinsViewModel : RoutableViewModel
{
	[AutoNotify] private ObservableAsPropertyHelper<bool>? _anySelected;
	[AutoNotify] private FlatTreeDataGridSource<WalletCoinViewModel> _source;

	private readonly WalletViewModel _walletViewModel;

	public WalletCoinsViewModel(WalletViewModel walletViewModel, IObservable<Unit> balanceChanged)
	{
		_walletViewModel = walletViewModel;
		SetupCancel(false, true, true);
		NextCommand = CancelCommand;
		
		SkipCommand = ReactiveCommand.CreateFromTask(OnSendCoins);
	}

	public bool IsAnySelected => _anySelected?.Value ?? false;

	private static int GetOrderingPriority(WalletCoinViewModel x)
	{
		if (x.CoinJoinInProgress)
		{
			return 1;
		}

		if (x.IsBanned)
		{
			return 2;
		}

		if (!x.Confirmed)
		{
			return 3;
		}

		return 0;
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		var initial = Observable.Return(GetCoins());

		var polling = Observable
			.Timer(TimeSpan.FromSeconds(2))
			.Repeat()
			.Select(_ => GetCoins());

		var source = initial.Concat(polling).Publish();

		var observable = source
			.ToObservableChangeSet(c => c.HdPubKey.GetHashCode())
			.AsObservableCache()
			.Connect()
			.TransformWithInlineUpdate(x => new WalletCoinViewModel(x))
			.Publish();

		_anySelected = observable
			.AutoRefresh(x => x.IsSelected)
			.ToCollection()
			.Select(items => items.Any(t => t.IsSelected))
			.ObserveOn(RxApp.MainThreadScheduler)
			.ToProperty(this, model => model.IsAnySelected)
			.DisposeWith(disposables);
		this.RaisePropertyChanged(nameof(IsAnySelected));

		observable
			.DisposeMany()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Bind(out var coins)
			.Subscribe()
			.DisposeWith(disposables);

		Source = CreateGridSource(coins)
			.DisposeWith(disposables);

		observable.Connect()
			.DisposeWith(disposables);

		source.Connect()
			.DisposeWith(disposables);

		base.OnNavigatedTo(isInHistory, disposables);
	}

	private async Task OnSendCoins()
	{
		var wallet = _walletViewModel.Wallet;
		var selectedSmartCoins = _source.Items.Where(x => x.IsSelected).Select(x => x.Coin).ToImmutableList();
		var info = new TransactionInfo(wallet.KeyManager.AnonScoreTarget);

		var addressDialog = new AddressEntryDialogViewModel(wallet.Network, info);
		var addressResult = await NavigateDialogAsync(addressDialog, NavigationTarget.CompactDialogScreen);
		if (addressResult.Result is not { } address)
		{
			return;
		}

		var labelDialog = new LabelEntryDialogViewModel(wallet, info);
		var result = await NavigateDialogAsync(labelDialog, NavigationTarget.CompactDialogScreen);
		if (result.Result is not { } label)
		{
			return;
		}

		info.Coins = selectedSmartCoins;
		info.Amount = selectedSmartCoins.Sum(x => x.Amount);
		info.SubtractFee = true;
		info.UserLabels = label;
		info.IsSelectedCoinModificationEnabled = false;

		Navigate().To(new TransactionPreviewViewModel(wallet, info, address, true));
	}

	private FlatTreeDataGridSource<WalletCoinViewModel> CreateGridSource(IEnumerable<WalletCoinViewModel> coins)
	{
		// [Column]			[View]					[Header]	[Width]		[MinWidth]		[MaxWidth]	[CanUserSort]
		// Selection		SelectionColumnView		-			Auto		-				-			false
		// Indicators		IndicatorsColumnView	-			Auto		-				-			true
		// Amount			AmountColumnView		Amount		Auto		-				-			true
		// AnonymityScore	AnonymityColumnView		<custom>	50			-				-			true
		// Labels			LabelsColumnView		Labels		*			-				-			true
		var source = new FlatTreeDataGridSource<WalletCoinViewModel>(coins)
		{
			Columns =
			{
				// Selection
				new TemplateColumn<WalletCoinViewModel>(
					null,
					new FuncDataTemplate<WalletCoinViewModel>((node, ns) => new SelectionColumnView(), true),
					options: new ColumnOptions<WalletCoinViewModel>
					{
						CanUserResizeColumn = false,
						CanUserSortColumn = false
					},
					width: new GridLength(0, GridUnitType.Auto)),

				// Indicators
				new TemplateColumn<WalletCoinViewModel>(
					null,
					new FuncDataTemplate<WalletCoinViewModel>((node, ns) => new IndicatorsColumnView(), true),
					options: new ColumnOptions<WalletCoinViewModel>
					{
						CanUserResizeColumn = false,
						CanUserSortColumn = true,
						CompareAscending = WalletCoinViewModel.SortAscending(x => GetOrderingPriority(x)),
						CompareDescending = WalletCoinViewModel.SortDescending(x => GetOrderingPriority(x))
					},
					width: new GridLength(0, GridUnitType.Auto)),

				// Amount
				new TemplateColumn<WalletCoinViewModel>(
					"Amount",
					new FuncDataTemplate<WalletCoinViewModel>((node, ns) => new AmountColumnView(), true),
					options: new ColumnOptions<WalletCoinViewModel>
					{
						CanUserResizeColumn = false,
						CanUserSortColumn = true,
						CompareAscending = WalletCoinViewModel.SortAscending(x => x.Amount),
						CompareDescending = WalletCoinViewModel.SortDescending(x => x.Amount)
					},
					width: new GridLength(0, GridUnitType.Auto)),

				// AnonymityScore
				new TemplateColumn<WalletCoinViewModel>(
					new AnonymitySetHeaderView(),
					new FuncDataTemplate<WalletCoinViewModel>((node, ns) => new AnonymitySetColumnView(), true),
					options: new ColumnOptions<WalletCoinViewModel>
					{
						CanUserResizeColumn = false,
						CanUserSortColumn = true,
						CompareAscending = WalletCoinViewModel.SortAscending(x => x.AnonymitySet),
						CompareDescending = WalletCoinViewModel.SortDescending(x => x.AnonymitySet)
					},
					width: new GridLength(50, GridUnitType.Pixel)),

				// Labels
				new TemplateColumn<WalletCoinViewModel>(
					"Labels",
					new FuncDataTemplate<WalletCoinViewModel>((node, ns) => new LabelsColumnView(), true),
					options: new ColumnOptions<WalletCoinViewModel>
					{
						CanUserResizeColumn = false,
						CanUserSortColumn = true,
						CompareAscending = WalletCoinViewModel.SortAscending(x => x.SmartLabel),
						CompareDescending = WalletCoinViewModel.SortDescending(x => x.SmartLabel)
					},
					width: new GridLength(1, GridUnitType.Star))
			}
		};

		source.RowSelection!.SingleSelect = true;

		return source;
	}

	private ICoinsView GetCoins()
	{
		return _walletViewModel.Wallet.Coins;
	}
}
