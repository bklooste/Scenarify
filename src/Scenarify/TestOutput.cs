namespace Scenarify;

public class TestOutput
{
    public void Write(string msg)
    {
        // xUnit v3: IMessageSink is removed. Output directly to console.
        Console.WriteLine(msg);
    }
}
