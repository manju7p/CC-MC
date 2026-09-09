namespace CCMC.Desktop.Windows;

/// <summary>A ComboBox item with a display label distinct from its underlying value (e.g. "RTS/CTS" for SerialFlowControl.RequestToSend).</summary>
public sealed record ComboOption<T>(string Label, T Value);
