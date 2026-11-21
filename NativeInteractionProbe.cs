using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace MauiInteractionsRepro;

// The Objective-C classes registered here have no managed type, so .NET can only reach their instances
// through a UIInteractionWrapper protocol wrapper, the way it reaches UIKit's private interactions.
static unsafe class NativeInteractionProbe
{
	const string watchedUIKitInteraction = "_UIKeyboardBasedTextSelectionInteraction";
	const nuint associationRetainNonatomic = 1;

	static readonly ConcurrentDictionary<string, IntPtr> registeredClasses = new();
	static readonly ConcurrentQueue<DeallocRecord> deallocs = new();
	static readonly IntPtr watcherAssociationKey = Selector.GetHandle("reproDeallocWatcher");
	static int watchedCount;

	public record DeallocRecord(string ClassName, bool OnMainThread, int ManagedThreadId, string StackTrace);

	public static void Reset()
	{
		deallocs.Clear();
		watchedCount = 0;
	}

	public static bool IsAttachingEnabled { get; set; }
	public static int WatchedCount => watchedCount;
	public static IReadOnlyCollection<DeallocRecord> Deallocs => deallocs;

	public static void AttachTo(UIView view, string className)
	{
		if (!IsAttachingEnabled)
		{
			return;
		}

		var interaction = CreateInstance(className);
		ObjC.SendVoid(view.Handle, "addInteraction:", interaction);
		ObjC.SendVoid(interaction, "release");
		Interlocked.Increment(ref watchedCount);

		WatchUIKitTextSelectionInteractions(view.Handle);
		SlowFinalizerGarbage.Create();
	}

	// The runtime releases an object's associated objects inside its dealloc, on the same thread, so the watcher's dealloc
	// shows where UIKit's own interaction deallocated without swizzling a private class.
	static void WatchUIKitTextSelectionInteractions(IntPtr view)
	{
		foreach (var interaction in InteractionsProbe.ReadWithoutLogging(view).Where(element => element.ClassName == watchedUIKitInteraction))
		{
			var watcher = CreateInstance($"DeallocWatcherOf{watchedUIKitInteraction}");
			ObjC.objc_setAssociatedObject(interaction.Handle, watcherAssociationKey, watcher, associationRetainNonatomic);
			ObjC.SendVoid(watcher, "release");
			Interlocked.Increment(ref watchedCount);
		}
	}

	static IntPtr CreateInstance(string className)
		=> ObjC.SendIntPtr(ObjC.SendIntPtr(registeredClasses.GetOrAdd(className, RegisterClass), "alloc"), "init");

	static IntPtr RegisterClass(string className)
	{
		var nativeClass = ObjC.objc_allocateClassPair(Class.GetHandle("NSObject"), className, 0);
		ObjC.class_addProtocol(nativeClass, ObjC.objc_getProtocol("UIInteraction"));
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("view"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&GetView, "@@:");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("willMoveToView:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MoveToView, "v@:@");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("didMoveToView:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MoveToView, "v@:@");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("dealloc"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Dealloc, "v@:");
		ObjC.objc_registerClassPair(nativeClass);
		return nativeClass;
	}

	[UnmanagedCallersOnly]
	static IntPtr GetView(IntPtr self, IntPtr selector) => IntPtr.Zero;

	[UnmanagedCallersOnly]
	static void MoveToView(IntPtr self, IntPtr selector, IntPtr view)
	{
	}

	[UnmanagedCallersOnly]
	static void Dealloc(IntPtr self, IntPtr selector)
	{
		deallocs.Enqueue(new DeallocRecord(ObjC.GetClassName(self), NSThread.IsMain, Environment.CurrentManagedThreadId, Environment.StackTrace));

		var super = new ObjC.Super { Receiver = self, SuperClass = Class.GetHandle("NSObject") };
		ObjC.objc_msgSendSuper(&super, selector);
	}
}

static unsafe class ObjC
{
	const string LibObjC = "/usr/lib/libobjc.dylib";

	public struct Super
	{
		public IntPtr Receiver;
		public IntPtr SuperClass;
	}

	[DllImport(LibObjC)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
	[DllImport(LibObjC)] public static extern void objc_registerClassPair(IntPtr cls);
	[DllImport(LibObjC)] public static extern IntPtr objc_getProtocol(string name);
	[DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
	[DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
	[DllImport(LibObjC)] public static extern void objc_msgSendSuper(Super* super, IntPtr selector);
	[DllImport(LibObjC)] public static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr selector);
	[DllImport(LibObjC)] public static extern IntPtr method_getImplementation(IntPtr method);
	[DllImport(LibObjC)] public static extern IntPtr method_setImplementation(IntPtr method, IntPtr implementation);
	[DllImport(LibObjC)] public static extern void objc_setAssociatedObject(IntPtr obj, IntPtr key, IntPtr value, nuint policy);
	[DllImport(LibObjC)] static extern IntPtr object_getClass(IntPtr obj);
	[DllImport(LibObjC)] static extern IntPtr class_getName(IntPtr cls);

	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIntPtrWithIndex(IntPtr receiver, IntPtr selector, nuint index);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid(IntPtr receiver, IntPtr selector);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

	public static IntPtr SendIntPtr(IntPtr receiver, string selector) => SendIntPtr(receiver, Selector.GetHandle(selector));
	public static nuint SendNUInt(IntPtr receiver, string selector) => SendNUInt(receiver, Selector.GetHandle(selector));
	public static IntPtr SendIntPtrWithIndex(IntPtr receiver, string selector, nuint index) => SendIntPtrWithIndex(receiver, Selector.GetHandle(selector), index);
	public static void SendVoid(IntPtr receiver, string selector) => SendVoid(receiver, Selector.GetHandle(selector));
	public static void SendVoid(IntPtr receiver, string selector, IntPtr argument) => SendVoid(receiver, Selector.GetHandle(selector), argument);

	public static string GetClassName(IntPtr obj) => Marshal.PtrToStringUTF8(class_getName(object_getClass(obj))) ?? "?";
}

// The finalizer thread runs finalizers one after another. Finalizable objects created after a view's peer and before
// MAUI's read delay the finalizers of the wrappers that read creates, which gives the main thread time to release
// the view first. A busy finalizer pass in a real app has the same effect.
sealed class SlowFinalizerGarbage
{
	const int objectsPerCall = 50;

	public static bool IsEnabled { get; set; }

	public static void Create()
	{
		if (!IsEnabled)
		{
			return;
		}

		for (var index = 0; index < objectsPerCall; index++)
		{
			_ = new SlowFinalizerGarbage();
		}
	}

	~SlowFinalizerGarbage() => Thread.Sleep(2);
}
