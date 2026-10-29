# Desktop UI icons

Use **Lucide**, through `Views/AppIcon.cs`, for desktop UI icons. See the desktop UI
section of [CLAUDE.md](../../CLAUDE.md) for the surrounding design conventions.

- In XAML, use `<views:AppIcon Kind="Terminal" Size="14" />`; in C#, use `AppIcon`
  with a `LucideIconKind`. Do not instantiate another icon renderer directly.
- Do not draw custom UI icon paths, copy SVG geometry into XAML/C#, use text or emoji
  as icons, or introduce another icon family. Choose the closest semantic Lucide icon.
- Reuse `ToolCategoryIcons` for chat categories and fixed headings, and the named
  mappings in `PullRequestStatus` for PR states. Give the same action the same icon
  across screens; distinguish an agent action from a report produced by an agent.
- Set `Size`, preserving Lucide's full 24px canvas and the shared stroke width:
  12px for dense status/header rows, 14px by default, and 16px for action buttons.
  Keep optical alignment adjustments in the shared header/control, not per icon.
- Existing vendor logos (`VendorIcons`), product branding (`ProductIcon`/wordmark),
  native control glyphs, and activity dots/pulses are intentional exceptions.
  Keyboard shortcuts, file extensions, and code remain text.
- Check icon choices and alignment in rendered UI at their actual sizes. Icons
  supplement readable labels; icon-only buttons need an accessible name.
