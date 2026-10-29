namespace Capacitor.App.Tests.Unit;

/// The figure a docs scene test renders; its capture names the output file from this alone.
[AttributeUsage(AttributeTargets.Method)]
sealed class DocsShotAttribute(string name) : Attribute {
    public string Name { get; } = name;
}
