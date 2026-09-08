using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using GuiColor = Terminal.Gui.Drawing.Color;

namespace Tedide.App.Views;

/// <summary>
/// A read-only "About" dialog for Help > About - the app name, a one-line description, its
/// credits/repository link, and the author's GitHub avatar on the right, dismissed with a single
/// OK button. Unlike every other dialog in the app, there's nothing to cancel (no input fields),
/// so it gets one centered primary button rather than the usual primary/Cancel pair.
/// </summary>
public sealed class AboutDialog : Dialog
{
    /// <summary>Width of the avatar <see cref="ImageView"/>, in character cells.</summary>
    private const int AvatarWidth = 24;

    /// <summary>Height of the avatar <see cref="ImageView"/>, in character cells - roughly half
    /// <see cref="AvatarWidth"/> rather than equal to it, since a terminal cell is taller than it
    /// is wide (~1:2 width:height in pixels), so a square source image needs about twice as many
    /// columns as rows to *look* square once rendered.</summary>
    private const int AvatarHeight = 10;

    public AboutDialog()
    {
        Title = "About Tedide";
        Width = 100; // 20% wider than the previous 83 (originally 30% wider than 64)
        Height = 18; // +2 rows over the previous 16, for the added versionLabel below
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        // Dialogs default to Movable|Resizable in Terminal.Gui (see ViewArrangement) - fixed size
        // here since there's nothing in this static, fixed-position layout that benefits from
        // resizing.
        Arrangement &= ~ViewArrangement.Resizable;

        // Text column is narrowed to leave room for the avatar on the right (its width plus a
        // 1-cell gap on each side, matching the "every field needs clearance" convention).
        var textWidth = Dim.Fill(AvatarWidth + 2);

        var titleLabel = new Label { Text = "Tedide - CC65 IDE", X = 0, Y = 0, Width = textWidth };

        var descriptionLabel = new Label
        {
            Text = "A terminal (TUI) IDE for cc65 development, modeled\nloosely on Visual Studio.",
            X = 0, Y = 2, Width = textWidth, Height = 2,
        };

        var creditsLabel = new Label
        {
            Text = "Built with Terminal.Gui and Terminal.Gui.Editor (tui-cs).",
            X = 0, Y = 5, Width = textWidth,
        };

        var repoLabel = new Label
        {
            Text = "https://github.com/madman1969/TedIDE",
            X = 0, Y = 7, Width = textWidth,
        };

        var versionLabel = new Label
        {
            Text = $"Version {AppVersion.Current}",
            X = 0, Y = 9, Width = textWidth,
        };

        var views = new List<View> { titleLabel, descriptionLabel, creditsLabel, repoLabel, versionLabel };

        // Null if the embedded resource is somehow missing - the dialog still shows its text
        // rather than failing to open at all.
        if (LoadAvatarImage() is { } avatarImage)
        {
            var avatarView = new ImageView
            {
                Image = avatarImage,
                X = Pos.AnchorEnd(AvatarWidth),
                Y = 0,
                Width = AvatarWidth,
                Height = AvatarHeight,
            };
            views.Add(avatarView);
        }

        // The only action: Accent-scheme so it visually pops against the dialog's normal chrome,
        // the same accent color the app uses for the menu bar's own highlighted items. Centered
        // alone, not paired with a Cancel, since there's nothing here to cancel.
        var okButton = new Button { Text = "_OK", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 6, Y = Pos.AnchorEnd(1), Width = 12 };
        okButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };
        views.Add(okButton);

        Add(views.ToArray());
    }

    /// <summary>
    /// Decodes the embedded avatar.jpg (the author's GitHub avatar, fetched once from
    /// github.com/madman1969.png and bundled with the app rather than fetched over the network
    /// every time this dialog opens) into the <c>Color[,]</c> pixel grid <see cref="ImageView.Image"/>
    /// expects - Terminal.Gui's ImageView deliberately has no image-decoding dependency of its own
    /// (see its own doc remarks), so decoding the file format is this app's job, via
    /// System.Drawing.Common. Returns null if the resource can't be found/decoded.
    /// </summary>
    private static GuiColor[,]? LoadAvatarImage()
    {
        using var stream = typeof(AboutDialog).Assembly.GetManifestResourceStream("Tedide.App.Resources.avatar.jpg");
        if (stream is null)
            return null;

        using var bitmap = new System.Drawing.Bitmap(stream);
        var pixels = new GuiColor[bitmap.Width, bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                pixels[x, y] = new GuiColor(pixel.R, pixel.G, pixel.B);
            }
        }
        return pixels;
    }
}
