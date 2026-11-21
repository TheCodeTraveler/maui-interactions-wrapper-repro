# MAUI UIView.Interactions wrapper repro

On iOS, .NET MAUI's `GesturePlatformManager.LoadRecognizers()` reads `UIView.Interactions` on every handler connect. .NET wraps each interaction that has no managed peer in a new `UIInteractionWrapper`, which releases its interaction from its finalizer, on the finalizer thread ([dotnet/macios#27420](https://github.com/dotnet/macios/issues/27420)). When that release is the last one, the UIKit interaction deallocs off the main thread.

The app uses only public Objective-C runtime API:

- It logs each read of `UIView.Interactions` made through the managed property: caller, window state, and each element's class and managed peer. The app never uses the managed property itself.
- It adds a native interaction with no managed type to each `Entry` and `Editor`, and watches UIKit's `_UIKeyboardBasedTextSelectionInteraction`. Each `dealloc` records its thread and stack.

## Run it

Needs an Apple silicon Mac with Xcode 27.2 and .NET SDK 11.0.100 with the iOS workload (workload set 11.0.100.1). The app uses Microsoft.Maui.Controls 11.0.10.

```sh
dotnet build -c Release
UDID=$(xcrun simctl create MauiInteractionsRepro "iPhone 16 Pro" com.apple.CoreSimulator.SimRuntime.iOS-18-6)
xcrun simctl bootstatus $UDID -b
xcrun simctl install $UDID bin/Release/net11.0-ios/iossimulator-arm64/MauiInteractionsRepro.app
xcrun simctl launch --console-pty --terminate-running-process $UDID com.example.mauiinteractionsrepro
```

- The app exits by itself after about 40 seconds.
- Any iOS simulator runtime works, with a device type that it supports. Delete the simulator afterwards with `xcrun simctl delete $UDID`.
- Workaround build: `dotnet build -c Release -p:NoGesturePlatformManager=true` registers a no-op `IGesturePlatformManagerFactory` ([NoGesturesPlatformManagerFactory.cs](NoGesturesPlatformManagerFactory.cs)), so MAUI never reads `Interactions`. Run it with the same steps.

## Scenarios

- **A:** show 10 `Entry` and 10 `Editor`, pop the page, collect.
- **B:** 30 times: create an `Entry` and an `Editor` handler off screen, call `DisconnectHandler`, drop them, and collect on the next run-loop turn.
- **C:** B, with slow finalizers queued ahead of the wrappers, so the finalizer thread lags behind the main thread.
- **D (control):** C's platform views without MAUI handlers, so nothing reads `Interactions`.
- **E:** B, plus `platformView.Dispose()` right after `DisconnectHandler`.

## Expected and actual

Compare the `READS`, `RESULT` and `STACK` lines.

- **Expected:** every interaction deallocs on the main thread, as in control D.
- **Actual:** MAUI reads `Interactions` on every handler connect. Interactions dealloc on the finalizer thread in C (a few per run) and E (every one). The workaround build makes no reads, and every dealloc stays on the main thread.
