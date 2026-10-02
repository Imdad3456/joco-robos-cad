using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace JocoRobos.Cad
{
    /// <summary>A small labelled form: one row per setting, OK and Cancel at the bottom.</summary>
    internal class ToolForm : Form
    {
        protected readonly TableLayoutPanel Rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(14, 12, 14, 4) };
        protected readonly Label Note = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0), Margin = new Padding(14, 0, 14, 8) };
        protected readonly Button Ok = new Button { Text = "Insert", Width = 100, Height = 30 };
        protected readonly Button Cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };

        protected ToolForm(string title, string okText = "Insert")
        {
            Text = "CAD Hub — " + title;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Ok.Text = okText;
            Rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 4, 10, 10) };
            buttons.Controls.Add(Cancel);
            buttons.Controls.Add(Ok);
            var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
            stack.Controls.Add(Rows);
            stack.Controls.Add(Note);
            stack.Controls.Add(buttons);
            Controls.Add(stack);
            AcceptButton = Ok;
            CancelButton = Cancel;
            Ok.Click += (s, e) =>
            {
                string problem = Check();
                if (problem != null) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        /// <summary>Why the settings can't be used, or null.</summary>
        protected virtual string Check() { return null; }

        protected T Row<T>(string label, T control) where T : Control
        {
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) };
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            Rows.Controls.Add(caption);
            Rows.Controls.Add(control);
            return control;
        }

        protected static ComboBox Choice(params object[] items)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(items);
            box.SelectedIndex = 0;
            return box;
        }

        protected static double? Number(TextBox box)
        {
            return StockParts.ParseInches(box.Text);
        }
    }

    internal sealed class BeltChainDialog : ToolForm
    {
        private readonly ComboBox kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown first = new NumericUpDown { Minimum = 8, Maximum = 120, Value = 18 };
        private readonly NumericUpDown second = new NumericUpDown { Minimum = 8, Maximum = 120, Value = 36 };
        private readonly NumericUpDown length = new NumericUpDown { Minimum = 10, Maximum = 2000, Value = 100 };
        private readonly TextBox wanted = new TextBox { Text = "" };
        private readonly Label answer = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(14, 4, 14, 8) };
        private readonly Func<double, string> useForDimension;
        private double center = double.NaN;

        internal BeltChainDialog(Func<double, string> useForDimension) : base("Belt and Chain Calculator", "Use for selected dimension")
        {
            this.useForDimension = useForDimension;
            foreach (var k in BeltChain.Kinds) kind.Items.Add(k.Name);
            kind.SelectedIndex = 0;
            Row("Belt or chain", kind);
            Row("Pulley/sprocket 1 (teeth)", first);
            Row("Pulley/sprocket 2 (teeth)", second);
            Row("Belt length (teeth)", length);
            Row("Or: center distance wanted (in)", wanted);
            ((FlowLayoutPanel)Rows.Parent).Controls.Add(answer);
            ((FlowLayoutPanel)Rows.Parent).Controls.SetChildIndex(answer, 1);
            Ok.Width = 200;
            Cancel.Text = "Close";
            Note.Text = "Center distance from the two tooth counts and the belt (teeth) or chain (links) length. Or type the center distance you want to see " +
                "the lengths either side. To set a sketch: click the center-distance dimension in SOLIDWORKS, then \"Use for selected dimension\".";
            EventHandler refresh = (s, e) => Calculate();
            kind.SelectedIndexChanged += (s, e) => { ((Label)Rows.GetControlFromPosition(0, 3)).Text = BeltChain.Kinds[kind.SelectedIndex].Chain ? "Chain length (links)" : "Belt length (teeth)"; Calculate(); };
            first.ValueChanged += refresh;
            second.ValueChanged += refresh;
            length.ValueChanged += refresh;
            wanted.TextChanged += refresh;
            Calculate();
        }

        private void Calculate()
        {
            var drive = BeltChain.Kinds[kind.SelectedIndex];
            var target = Number(wanted);
            if (target != null && target > 0)
            {
                var near = BeltChain.NearestLengths(drive, (int)first.Value, (int)second.Value, target.Value);
                double shorter = BeltChain.CenterDistance(drive, (int)first.Value, (int)second.Value, near.Item1);
                double longer = BeltChain.CenterDistance(drive, (int)first.Value, (int)second.Value, near.Item2);
                answer.Text = "For " + StockParts.Inches(target.Value) + " in: " + near.Item1 + " " + drive.Unit + " (center " + Show(shorter) + ") or " +
                    near.Item2 + " " + drive.Unit + " (center " + Show(longer) + ").";
                center = double.NaN;
                Ok.Enabled = false;
                return;
            }
            center = BeltChain.CenterDistance(drive, (int)first.Value, (int)second.Value, (int)length.Value);
            answer.Text = double.IsNaN(center) ? "Too short to go around both." : "Center distance: " + Show(center) + " in (" +
                (center * 25.4).ToString("0.00", CultureInfo.InvariantCulture) + " mm)";
            Ok.Enabled = !double.IsNaN(center);
        }

        private static string Show(double inches) { return double.IsNaN(inches) ? "—" : inches.ToString("0.000", CultureInfo.InvariantCulture); }

        protected override string Check()
        {
            if (double.IsNaN(center)) return "Nothing to use yet.";
            return useForDimension(center);
        }
    }
}
