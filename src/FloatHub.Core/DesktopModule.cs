using System;
using System.Collections.Generic;
using System.Windows;

namespace FloatHub.Core;

public record SettingsPage(string Title, string Section, string Glyph, Func<FrameworkElement> Create);
public interface IDesktopModule : IDisposable
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    FrameworkElement View { get; }
    IReadOnlyList<SettingsPage> Pages { get; }
    void Initialize(Store storage, Journal log, Action<string> openPage);
    void LoadConfiguration();
    void SetEnabled(bool enabled);
    void SetPresented(bool presented);
}
