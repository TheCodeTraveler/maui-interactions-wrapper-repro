using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;

namespace MauiInteractionsRepro;

// Replaces the Objective-C getter behind UIView.Interactions, so every read is logged before
// .NET marshals the array. Elements are inspected with raw messages: a managed read would itself
// create the throwaway protocol wrappers this repro is about.
static unsafe class InteractionsProbe
{
	static IntPtr originalGetter;
	static int readNumber;
	static int nativeReadCount;
	static readonly ConcurrentDictionary<string, int[]> countsPerStepReceiverAndCaller = new();

	[ThreadStatic] static bool isSuppressed;

	public static string CurrentStep { get; set; } = "startup";
	public static int ManagedReadCount => readNumber;

	public static void InstallInteractionsGetterHook()
	{
		var method = ObjC.class_getInstanceMethod(Class.GetHandle("UIView"), Selector.GetHandle("interactions"));
		originalGetter = ObjC.method_getImplementation(method);
		ObjC.method_setImplementation(method, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&InteractionsGetter);
	}

	public static List<(IntPtr Handle, string ClassName, bool HasManagedPeer)> ReadWithoutLogging(IntPtr view)
	{
		isSuppressed = true;
		try
		{
			return Describe(ObjC.SendIntPtr(view, "interactions"));
		}
		finally
		{
			isSuppressed = false;
		}
	}

	public static void PrintCountsPerStep()
	{
		Console.WriteLine($"COUNT reads made by UIKit itself, not through the managed UIView.Interactions property (not logged): {nativeReadCount}");
		foreach (var (key, counts) in countsPerStepReceiverAndCaller.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			Console.WriteLine($"COUNT {key}: reads={counts[0]}, interactions={counts[1]}, without managed peer={counts[2]}");
		}
	}

	[UnmanagedCallersOnly]
	static IntPtr InteractionsGetter(IntPtr view, IntPtr selector)
	{
		var interactions = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)originalGetter)(view, selector);
		if (isSuppressed)
		{
			return interactions;
		}

		var caller = GetManagedCaller();
		if (caller is null)
		{
			Interlocked.Increment(ref nativeReadCount);
			return interactions;
		}

		var elements = Describe(interactions);
		var receiver = $"{ObjC.GetClassName(view)} ({(ObjC.SendIntPtr(view, "window") == IntPtr.Zero ? "not in a window" : "in a window")})";
		var number = Interlocked.Increment(ref readNumber);
		var withoutPeer = elements.Where(element => !element.HasManagedPeer).Select(element => element.ClassName).ToList();

		Console.WriteLine($"READ #{number} [{CurrentStep}] receiver {receiver} from {caller}: {elements.Count} interactions, " +
			$"{withoutPeer.Count} without managed peer: {string.Join(", ", withoutPeer)}; all: {string.Join(", ", elements.Select(element => element.ClassName))}");

		var counts = countsPerStepReceiverAndCaller.GetOrAdd($"[{CurrentStep}] {receiver} from {caller.Split(" <- ")[0]}", _ => new int[3]);
		Interlocked.Increment(ref counts[0]);
		Interlocked.Add(ref counts[1], elements.Count);
		Interlocked.Add(ref counts[2], withoutPeer.Count);

		return interactions;
	}

	static List<(IntPtr Handle, string ClassName, bool HasManagedPeer)> Describe(IntPtr nativeArray)
	{
		var elements = new List<(IntPtr, string, bool)>();
		if (nativeArray == IntPtr.Zero)
		{
			return elements;
		}

		var count = (int)ObjC.SendNUInt(nativeArray, "count");
		for (var index = 0; index < count; index++)
		{
			var element = ObjC.SendIntPtrWithIndex(nativeArray, "objectAtIndex:", (nuint)index);
			elements.Add((element, ObjC.GetClassName(element), Runtime.TryGetNSObject(element) is not null));
		}

		return elements;
	}

	// Reads that UIKit makes internally also reach the hook; only those that come through the
	// managed UIView.Interactions property can create wrappers, so only those are reported.
	static string? GetManagedCaller()
	{
		var frames = Environment.StackTrace
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line)
			.Select(line => line.Split(" in ")[0])
			.SkipWhile(frame => !frame.StartsWith("UIKit.UIView.get_Interactions", StringComparison.Ordinal))
			.Skip(1)
			.Take(5)
			.ToList();

		return frames.Count > 0 ? string.Join(" <- ", frames) : null;
	}
}
