namespace cc.Models;

public class WindowState
{
    public int Id { get; set; }
    public string Panel { get; set; } = "";
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Minimized { get; set; }
    public bool Maximized { get; set; }
}
