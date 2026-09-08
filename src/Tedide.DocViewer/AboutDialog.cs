using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using GuiColor = Terminal.Gui.Drawing.Color;

namespace Tedide.DocViewer;

/// <summary>
/// A read-only "About" dialog for Help > About - copied from Tedide.App's own AboutDialog with
/// just the displayed text changed to describe the Doc Viewer rather than the IDE (see that
/// class's doc comment for the rest of the design rationale, which applies unchanged here: no
/// Cancel button since there's nothing to cancel, avatar decoded via System.Drawing.Common since
/// ImageView has no image-decoding dependency of its own).
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
        Title = "About Tedide DocViewer";
        Width = 100;
        Height = 16;
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

        var titleLabel = new Label { Text = "Tedide DocViewer - CC65 Documentation", X = 0, Y = 0, Width = textWidth };

        var descriptionLabel = new Label
        {
            Text = "A terminal (TUI) viewer for cc65's documentation, with\nfull-text search and bookmarks.",
            X = 0, Y = 2, Width = textWidth, Height = 2,
        };

        var creditsLabel = new Label
        {
            Text = "Built with Terminal.Gui (tui-cs).",
            X = 0, Y = 5, Width = textWidth,
        };

        var repoLabel = new Label
        {
            Text = "https://github.com/madman1969/TedIDE",
            X = 0, Y = 7, Width = textWidth,
        };

        var views = new List<View> { titleLabel, descriptionLabel, creditsLabel, repoLabel };

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
    /// Decodes the embedded avatar.jpg (the same image Tedide.App's AboutDialog uses, duplicated
    /// into this project's own Resources folder so each app builds independently) into the
    /// <c>Color[,]</c> pixel grid <see cref="ImageView.Image"/> expects - Terminal.Gui's ImageView
    /// deliberately has no image-decoding dependency of its own (see its own doc remarks), so
    /// decoding the file format is this app's job, via System.Drawing.Common. Returns null if the
    /// resource can't be found/decoded.
    /// </summary>
    private static GuiColor[,]? LoadAvatarImage()
    {
        using var stream = typeof(AboutDialog).Assembly.GetManifestResourceStream("Tedide.DocViewer.Resources.avatar.jpg");
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
