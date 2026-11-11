using System.Globalization;
using System.Runtime.CompilerServices;

namespace OrchardSound;

public sealed class LiveNumericUpDown : NumericUpDown
{
    bool typing;
    protected override void OnTextChanged(EventArgs e)
    {
        typing = true;
        try { base.OnTextChanged(e); }
        finally { typing = false; UserEdit = false; }
    }
    protected override void UpdateEditText() { if (!typing) base.UpdateEditText(); }
    protected override void OnLeave(EventArgs e)
    {
        if (decimal.TryParse(Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value))
            Value = decimal.Round(Math.Clamp(value, Minimum, Maximum), DecimalPlaces);
        base.OnLeave(e); UpdateEditText();
    }
}

public static class LiveNumeric
{
    static readonly ConditionalWeakTable<NumericUpDown, State> attached = new();
    public static void Configure(NumericUpDown number) => attached.GetValue(number, control => new State(control));
    sealed class State
    {
        bool changing;
        public State(NumericUpDown number)
        {
            number.TextChanged += (_, _) =>
            {
                if (changing || !decimal.TryParse(number.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value) || value < number.Minimum || value > number.Maximum) return;
                changing = true;
                try
                {
                    var edit = number.Controls.OfType<TextBox>().FirstOrDefault();
                    int caret = edit?.SelectionStart ?? 0, length = edit?.SelectionLength ?? 0;
                    number.Value = decimal.Round(value, number.DecimalPlaces);
                    if (edit != null) { edit.SelectionStart = Math.Min(caret, edit.TextLength); edit.SelectionLength = Math.Min(length, edit.TextLength - edit.SelectionStart); }
                }
                finally { changing = false; }
            };
        }
    }
}
