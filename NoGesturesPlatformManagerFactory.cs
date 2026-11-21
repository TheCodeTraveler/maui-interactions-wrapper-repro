#if NO_GESTURE_PLATFORM_MANAGER
using Microsoft.Maui.Controls.Platform;

namespace MauiInteractionsRepro;

// The workaround from the issue: MAUI's GesturePlatformManager never runs, so nothing reads UIView.Interactions.
// It also turns off every Controls gesture, so it only fits apps without gestures.
sealed class NoGesturesPlatformManagerFactory : IGesturePlatformManagerFactory
{
	public IGesturePlatformManager CreateGesturePlatformManager(IViewHandler handler) => new NoGesturesPlatformManager();

	sealed class NoGesturesPlatformManager : IGesturePlatformManager
	{
		public void Dispose()
		{
		}
	}
}
#endif
