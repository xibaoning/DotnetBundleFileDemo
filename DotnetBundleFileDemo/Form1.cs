using System.Text;

namespace DotnetBundleFileDemo;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();

    }

    private void Form1_Load(object sender, EventArgs e)
    {
        this.Text = $"来自DLL: {new ClassLibrary1.Class1().Title}";
    }

    private void button1_Click(object sender, EventArgs e)
    {
        button1.Enabled = false;

        using var source = File.OpenRead("DotnetBundleFileDemo.exe");
        if (!DotNetBundleFile.IsBundle(source, out var headerOffset, out var headerPosition) || headerOffset == 0)
        {
            MessageBox.Show("不是有效的Bundle文件,或处于debug");
            return;
        }

        using var target = File.Create("DotnetBundleFileDemo2.exe");
        using var resource = File.OpenRead("test.txt");
        if (DotNetBundleFile.TryAdd(source, target, resource, "test.txt"))
        {
            MessageBox.Show("成功");
        }
        else
        {
            MessageBox.Show("失败");
        }
    }

    private void button2_Click(object sender, EventArgs e)
    {
        button2.Enabled = false;

        using var target = File.OpenRead("DotnetBundleFileDemo2.exe");
        using var resource = new MemoryStream();

        if (!DotNetBundleFile.TryRead(target, resource, "test.txt"))
        {
            MessageBox.Show("失败");
            return;
        }

        richTextBox1.Text = Encoding.Default.GetString(resource.ToArray());

    }
}
