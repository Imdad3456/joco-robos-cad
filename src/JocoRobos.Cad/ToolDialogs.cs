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

    /// <summary>Make a stock part: type, size, how many.</summary>
    internal sealed class StockDialog : ToolForm
    {
        private readonly ComboBox type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox length = new TextBox { Text = "12" };
        private readonly TextBox width = new TextBox { Text = "6" };
        private readonly NumericUpDown copies = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 1 };
        private Label lengthLabel, widthLabel;

        internal StockType Type { get { return StockParts.Types[type.SelectedIndex]; } }
        internal double? Length { get { return Number(length); } }
        internal double? Width { get { return Number(width); } }
        internal int Copies { get { return (int)copies.Value; } }

        internal StockDialog() : base("Make a Stock Part")
        {
            foreach (var t in StockParts.Types) type.Items.Add(t.Group + ": " + t.Label);
            type.SelectedIndex = 0;
            Row("Part", type);
            Row("Length (in)", length);
            Row("Width (in)", width);
            Row("How many", copies);
            lengthLabel = (Label)Rows.GetControlFromPosition(0, 1);
            widthLabel = (Label)Rows.GetControlFromPosition(0, 2);
            type.SelectedIndexChanged += (s, e) => ShowFields();
            Note.Text = "Built in SOLIDWORKS right away (no Onshape). Each kind is one part in the robot (90_COTS/Stock) with a configuration per size: " +
                "a new size is added for the whole team. Check one against the real part before cutting.";
            ShowFields();
        }

        private void ShowFields()
        {
            length.Enabled = lengthLabel.Enabled = Type.HasLength;
            width.Enabled = widthLabel.Enabled = Type.HasWidth;
        }

        protected override string Check() { return StockParts.Problem(Type, Length, Width); }
    }

    /// <summary>Spur gear: teeth, pitch, pressure angle, width, bore.</summary>
    internal sealed class GearDialog : ToolForm
    {
        private readonly NumericUpDown teeth = new NumericUpDown { Minimum = 8, Maximum = 200, Value = 36 };
        private readonly ComboBox pitch = Choice("20 DP (most FRC gears)", "32 DP", "10 DP");
        private readonly ComboBox pressure = Choice("20°", "14.5°");
        private readonly TextBox face = new TextBox { Text = "0.5" };
        private readonly ComboBox bore = Choice("1/2\" hex", "3/8\" hex", "1/2\" round", "8 mm round", "None");
        private readonly NumericUpDown copies = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 1 };

        internal int Teeth { get { return (int)teeth.Value; } }
        internal double Pitch { get { return new[] { 20.0, 32, 10 }[pitch.SelectedIndex]; } }
        internal double Pressure { get { return pressure.SelectedIndex == 0 ? 20 : 14.5; } }
        internal double FaceWidth { get { return Number(face) ?? 0; } }
        internal bool BoreHex { get { return bore.SelectedIndex <= 1; } }
        internal double Bore { get { return new[] { 0.5 + 0.004, 0.375 + 0.004, 0.5 + 0.002, 8 / 25.4 + 0.002, 0 }[bore.SelectedIndex]; } }
        internal string BoreName { get { return new[] { "0.5 Hex Bore", "0.375 Hex Bore", "0.5 Round Bore", "8mm Round Bore", "No Bore" }[bore.SelectedIndex]; } }
        internal int Copies { get { return (int)copies.Value; } }

        internal GearDialog() : base("Spur Gear")
        {
            Row("Teeth", teeth);
            Row("Diametral pitch", pitch);
            Row("Pressure angle", pressure);
            Row("Face width (in)", face);
            Row("Bore", bore);
            Row("How many", copies);
            Note.Text = "Check the pitch and pressure angle against the gear it meshes with (vendors list both). The gear is saved in the robot (90_COTS/Stock/Gears) for the whole team.";
        }

        protected override string Check()
        {
            if (FaceWidth <= 0 || FaceWidth > 4) return "Face width from 0.05 to 4 in.";
            return SpurGear.Problem(Teeth, Pitch, Pressure);
        }
    }

    /// <summary>Lighten Plate: rib, border, ring around holes, corner radius, smallest pocket, depth.</summary>
    internal sealed class LightenDialog : ToolForm
    {
        private readonly TextBox rib = new TextBox { Text = "0.15" };
        private readonly TextBox border = new TextBox { Text = "0.25" };
        private readonly TextBox ring = new TextBox { Text = "0.15" };
        private readonly TextBox corner = new TextBox { Text = "0.0625" };
        private readonly TextBox smallest = new TextBox { Text = "0.35" };
        private readonly TextBox depth = new TextBox { Text = "through" };

        internal LightenSettings Settings
        {
            get
            {
                return new LightenSettings { Rib = Number(rib) ?? 0.15, Border = Number(border) ?? 0.25, Ring = Number(ring) ?? 0.15,
                    CornerRadius = Number(corner) ?? 0.0625, MinPocket = Number(smallest) ?? 0.35 };
            }
        }

        /// <summary>0: through the plate.</summary>
        internal double Depth { get { return depth.Text.Trim().StartsWith("t", StringComparison.OrdinalIgnoreCase) ? 0 : Number(depth) ?? 0; } }

        internal LightenDialog() : base("Lighten Plate", "Lighten")
        {
            Row("Rib width (in)", rib);
            Row("Edge border (in)", border);
            Row("Ring around holes (in)", ring);
            Row("Corner radius (in)", corner);
            Row("Smallest pocket (in)", smallest);
            Row("Depth (in, or \"through\")", depth);
            Note.Text = "Ribs connect the plate's holes and corners in triangles; each triangle becomes a pocket with rounded corners (use your router bit's radius). " +
                "It's an ordinary sketch and cut: undo, edit, or delete it like any feature. Add holes or sketch points first where you want ribs to meet.";
        }

        protected override string Check()
        {
            foreach (var box in new[] { rib, border, ring, corner, smallest })
            {
                var value = Number(box);
                if (value == null || value <= 0 || value > 5) return "Each setting is a number of inches greater than 0, like 0.15.";
            }
            if (Depth < 0 || (Depth == 0 && !depth.Text.Trim().StartsWith("t", StringComparison.OrdinalIgnoreCase))) return "Depth: a number of inches, or \"through\".";
            return null;
        }
    }

    /// <summary>Belt and chain calculator: center distance from teeth and length, or the lengths for a center distance.</summary>
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

        internal BeltChainDialog(Func<double, string> useForDimension) : base("Belt & Chain Calculator", "Use for selected dimension")
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
