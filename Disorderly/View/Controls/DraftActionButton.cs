using System.Windows.Controls;
using System.Windows.Input;
using Disorderly.Services;

namespace Disorderly.View.Controls;

/// <summary>A physical press stays attached to its original identified draft, even across a render.</summary>
public sealed class DraftActionButton : Button
{
    private PaletteDraftEventArgs? captured;
    private bool physical;
    public PaletteDraftEventArgs? Action
    {
        get;
        set
        {
            if (field?.ActivationId != value?.ActivationId || field?.Action != value?.Action
                || field?.DraftId != value?.DraftId || field?.Generation != value?.Generation)
            {
                field = value;
            }
        }
    }
    public event EventHandler<PaletteDraftEventArgs>? ActionRequested;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        if (args.ClickCount > 1)
        {
            args.Handled = true;
            return;
        }

        this.Begin();
        base.OnPreviewMouseLeftButtonDown(args);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        if (args.Key is Key.Enter or Key.Space)
        {
            if (args.IsRepeat)
            {
                args.Handled = true;
                return;
            }

            this.Begin();
        }

        base.OnPreviewKeyDown(args);
    }

    private void Begin()
    {
        this.physical = true;
        this.captured = this.Action;
    }

    protected override void OnClick()
    {
        PaletteDraftEventArgs? action = this.physical ? this.captured : this.Action;
        bool valid = action is not null && (!this.physical || ReferenceEquals(action, this.Action));
        this.physical = false;
        this.captured = null;
        if (valid)
        {
            this.ActionRequested?.Invoke(this, action!);
            base.OnClick();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        this.physical = false;
        this.captured = null;
        this.ReleaseMouseCapture();
        base.OnLostKeyboardFocus(args);
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            this.captured = null; // Keep the canceled physical marker until a new press/click.
        }

        base.OnLostMouseCapture(args);
    }
}