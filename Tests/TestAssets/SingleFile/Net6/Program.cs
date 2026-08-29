using System;
using SingleFile.Dependency;

namespace SingleFile.App {
	internal static class Program {
		private static void Main() => Console.WriteLine("BUNDLE_VALUE=" + BundleValue.Value);
	}
}
