using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Platform;

namespace MauiInteractionsRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		InteractionsProbe.InstallInteractionsGetterHook();

		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();
#if NO_GESTURE_PLATFORM_MANAGER
		builder.Services.AddSingleton<IGesturePlatformManagerFactory, NoGesturesPlatformManagerFactory>();
#endif
		builder.ConfigureMauiHandlers(handlers =>
		{
			handlers.AddHandler<Entry, InteractionCarryingEntryHandler>();
			handlers.AddHandler<Editor, InteractionCarryingEditorHandler>();
		});

		return builder.Build();
	}
}

// The platform view is created after HandlerChanging and before MAUI builds its GesturePlatformManager. Attaching the
// interaction in CreatePlatformView puts it on the view before MAUI's first read of UIView.Interactions.
class InteractionCarryingEntryHandler : EntryHandler
{
	protected override MauiTextField CreatePlatformView()
	{
		var platformView = base.CreatePlatformView();
		NativeInteractionProbe.AttachTo(platformView, "ReproInteractionEntry");
		return platformView;
	}
}

class InteractionCarryingEditorHandler : EditorHandler
{
	protected override MauiTextView CreatePlatformView()
	{
		var platformView = base.CreatePlatformView();
		NativeInteractionProbe.AttachTo(platformView, "ReproInteractionEditor");
		return platformView;
	}
}
