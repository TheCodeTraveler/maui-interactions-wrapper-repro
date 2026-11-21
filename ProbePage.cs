using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using UIKit;

namespace MauiInteractionsRepro;

public class ProbePage : ContentPage
{
	const int viewsPerTypeInBurst = 10;
	const int cyclesInDiscardScenarios = 30;

	bool hasStarted;

	public ProbePage() => Content = new Label { Text = "Running, see the console output" };

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (hasStarted)
		{
			return;
		}

		hasStarted = true;
		Console.WriteLine($"ENV {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; Mono runtime = {Type.GetType("Mono.RuntimeStructs") is not null}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
		await Task.Delay(1000);
		await RunScenarioAsync();
		InteractionsProbe.PrintCountsPerStep();
		Console.WriteLine("DONE");
		Environment.Exit(0);
	}

	async Task RunScenarioAsync()
	{
		var label = new Label { Text = "Label" };
		var entry = new Entry { Text = "Entry" };
		var editor = new Editor { Text = "Editor", HeightRequest = 80 };

		await ShowStepAsync("1 create and show Label, Entry, Editor without gestures", new ContentPage { Content = new VerticalStackLayout { label, entry, editor } });
		PrintPrivateInteractions("after show", entry, editor);
		entry.Focus();
		await Task.Delay(1000);
		PrintPrivateInteractions("entry focused", entry, editor);
		editor.Focus();
		await Task.Delay(1000);
		PrintPrivateInteractions("editor focused", entry, editor);

		InteractionsProbe.CurrentStep = "2 add a TapGestureRecognizer to Label, Entry, Editor";
		var recognizers = new View[] { label, entry, editor }.Select(view => (view, recognizer: new TapGestureRecognizer())).ToList();
		recognizers.ForEach(pair => pair.view.GestureRecognizers.Add(pair.recognizer));
		await Task.Delay(500);

		InteractionsProbe.CurrentStep = "3 remove the TapGestureRecognizer again";
		recognizers.ForEach(pair => pair.view.GestureRecognizers.Remove(pair.recognizer));
		await Task.Delay(500);

		InteractionsProbe.CurrentStep = "4 pop the page (teardown)";
		await Navigation.PopAsync();
		await Task.Delay(500);

		await RunNativeInteractionLifetimeStepAsync();
	}

	async Task ShowStepAsync(string step, Page page)
	{
		InteractionsProbe.CurrentStep = step;
		await Navigation.PushAsync(page);
		await Task.Delay(1500);
	}

	static void PrintPrivateInteractions(string moment, Entry entry, Editor editor)
	{
		foreach (var (name, view) in new (string, VisualElement)[] { ("Entry", entry), ("Editor", editor) })
		{
			var interactions = InteractionsProbe.ReadWithoutLogging(((UIView)view.Handler!.PlatformView!).Handle);
			Console.WriteLine($"SNAPSHOT {moment}, {name} ({view.Handler.PlatformView!.GetType().Name}) has {interactions.Count} interactions: " +
				string.Join(", ", interactions.Select(interaction => $"{interaction.ClassName}{(interaction.HasManagedPeer ? "" : " (no managed peer)")}")));
		}
	}

	async Task RunNativeInteractionLifetimeStepAsync()
	{
		NativeInteractionProbe.Reset();
		NativeInteractionProbe.IsAttachingEnabled = true;
		await ShowStepAsync("5a show views that carry a native interaction without a managed type", CreateBurstPage());
		NativeInteractionProbe.IsAttachingEnabled = false;

		InteractionsProbe.CurrentStep = "5b pop the page, drop all references";
		await Navigation.PopAsync();
		await Task.Delay(500);
		await CollectAndReportAsync("A views shown for 1.5 s, popped, then collected");

		var mauiContext = Handler!.MauiContext!;
		await RunDiscardCyclesAsync("B Entry and Editor created and discarded without being shown, collected on the next run-loop turn", widenRaceWindow: false, () => CreateAndDiscardViews(mauiContext));
		await RunDiscardCyclesAsync("C same as B, with slow finalizers queued between each view and MAUI's read", widenRaceWindow: true, () => CreateAndDiscardViews(mauiContext));
		await RunDiscardCyclesAsync("D control: same as C, but the same platform views get no MAUI handler, so nothing reads Interactions", widenRaceWindow: true, CreateAndDiscardPlatformViewsWithoutHandlers);
		await RunDiscardCyclesAsync("E same as B, but each platform view is disposed right after its handler disconnects", widenRaceWindow: false, () => CreateAndDiscardViews(mauiContext, disposePlatformView: true));
	}

	async Task RunDiscardCyclesAsync(string scenario, bool widenRaceWindow, Action createAndDiscardViews)
	{
		NativeInteractionProbe.Reset();
		SlowFinalizerGarbage.IsEnabled = widenRaceWindow;
		var managedReadsBefore = InteractionsProbe.ManagedReadCount;
		var cyclesWithoutCollectionBeforeDiscard = 0;
		for (var cycle = 1; cycle <= cyclesInDiscardScenarios; cycle++)
		{
			NativeInteractionProbe.IsAttachingEnabled = true;
			InteractionsProbe.CurrentStep = $"6{scenario[0]} create and discard views without showing them";
			var collectionsBefore = GC.CollectionCount(0);
			createAndDiscardViews();
			cyclesWithoutCollectionBeforeDiscard += GC.CollectionCount(0) == collectionsBefore ? 1 : 0;
			NativeInteractionProbe.IsAttachingEnabled = false;

			// UIKit frees the views only after this run-loop turn ends; a natural collection also comes later, not in the same turn.
			await Task.Delay(50);
			GC.Collect();
			await Task.Delay(100);
		}

		SlowFinalizerGarbage.IsEnabled = false;
		Console.WriteLine($"GCCOUNT scenario {scenario[0]}: {cyclesWithoutCollectionBeforeDiscard} of {cyclesInDiscardScenarios} cycles had no garbage collection between creating and discarding the views");
		Console.WriteLine($"READS scenario {scenario[0]}: {InteractionsProbe.ManagedReadCount - managedReadsBefore} reads of UIView.Interactions through the managed property");
		await CollectAndReportAsync(scenario);
	}

	static async Task CollectAndReportAsync(string scenario)
	{
		Console.WriteLine($"GCCOUNT before the explicit collections: gen0={GC.CollectionCount(0)}");
		for (var round = 1; round <= 60 && NativeInteractionProbe.Deallocs.Count < NativeInteractionProbe.WatchedCount; round++)
		{
			// Waiting for finalizers on the main thread would keep it from releasing views while the finalizer thread runs,
			// which hides the race; an app's main thread keeps running its run loop.
			GC.Collect();
			await Task.Delay(300);
		}

		var records = NativeInteractionProbe.Deallocs;
		var onMain = records.Count(record => record.OnMainThread);
		Console.WriteLine($"RESULT scenario {scenario}: objects watched = {NativeInteractionProbe.WatchedCount}, deallocated = {records.Count}, " +
			$"dealloc on main thread = {onMain}, dealloc off the main thread = {records.Count - onMain}");

		foreach (var group in records.GroupBy(record => record.ClassName).OrderBy(group => group.Key, StringComparer.Ordinal))
		{
			Console.WriteLine($"RESULT scenario {scenario[0]}: {group.Key}: dealloc on main thread = {group.Count(record => record.OnMainThread)}, " +
				$"off the main thread = {group.Count(record => !record.OnMainThread)}");
		}

		foreach (var group in records.Where(record => !record.OnMainThread).GroupBy(record => (record.ManagedThreadId, record.StackTrace)))
		{
			Console.WriteLine($"STACK of {group.Count()} off-main-thread deallocs in scenario {scenario[0]} (managed thread {group.Key.ManagedThreadId}):\n{group.Key.StackTrace}");
		}
	}

	// NoInlining keeps the views out of the caller's frame, so the garbage collector can collect them after this returns.
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	static void CreateAndDiscardViews(IMauiContext mauiContext, bool disposePlatformView = false)
	{
		foreach (VisualElement view in new VisualElement[] { new Entry(), new Editor() })
		{
			var platformView = (UIView)view.ToHandler(mauiContext).PlatformView!;
			view.Handler!.DisconnectHandler();
			if (disposePlatformView)
			{
				platformView.Dispose();
			}
		}
	}

	// Without a handler there is no GesturePlatformManager, so nothing reads Interactions: the only difference from scenario C.
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	static void CreateAndDiscardPlatformViewsWithoutHandlers()
	{
		NativeInteractionProbe.AttachTo(new MauiTextField(), "ReproInteractionEntry");
		NativeInteractionProbe.AttachTo(new MauiTextView(), "ReproInteractionEditor");
	}

	// NoInlining keeps the views out of the caller's frame, so the garbage collector can collect them after the pop.
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	static Page CreateBurstPage()
	{
		var layout = new VerticalStackLayout();
		for (var index = 0; index < viewsPerTypeInBurst; index++)
		{
			layout.Add(new Entry { Text = $"Entry {index}" });
			layout.Add(new Editor { Text = $"Editor {index}", HeightRequest = 30 });
		}

		return new ContentPage { Content = new ScrollView { Content = layout } };
	}
}
