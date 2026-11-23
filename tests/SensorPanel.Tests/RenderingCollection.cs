namespace SensorPanel.Tests;

/// <summary>
/// The tests that share static state, which xUnit runs one at a time. Join it with
/// [Collection(RenderingCollection.Name)] when a test draws (the font cache in Fonts is a plain
/// Dictionary, and ImageWidget shares one paint) or sets <see cref="Layout.TextWidget.Now"/>,
/// which every clock that is drawn reads.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RenderingCollection { public const string Name = "Rendering"; }
