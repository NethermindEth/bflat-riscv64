// The console of an Airbender guest goes through the platform's quasi-UART
// (zkvm_platform_write), and a run that returns 0 ends with the output words.
class Program
{
    static int Main()
    {
        System.Console.WriteLine("Hello from Airbender");
        return 0;
    }
}
