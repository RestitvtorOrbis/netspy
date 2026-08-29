using SingleFile.Dependency;

// Keep the value in a referenced project so every fixture has a second managed
// assembly that the reader can locate and compare byte-for-byte.
Console.WriteLine($"BUNDLE_VALUE={BundleValue.Value}");
