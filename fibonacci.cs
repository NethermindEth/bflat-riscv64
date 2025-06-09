using System;

class Program 
{
    static int Main() 
    {
        byte[] input = ZisK.ReadInput();
        
        if (input.Length < 8) 
        {
            ZisK.WriteLine("Error: Input too short");
            return -1;
        }
        
        ulong n = ZisK.ReadUInt64(input);
        long result = Fibonacci((int)n);
        
        ZisK.SetOutput64(0, (ulong)result);
        ZisK.WriteLine("Fibonacci computed");
        
        return 0;
    }
    
    static long Fibonacci(int n) 
    {
        if (n <= 1) return n;
        
        long prev = 0, current = 1;
        for (int i = 2; i <= n; i++) {
            long next = prev + current;
            prev = current;
            current = next;
        }
        return current;
    }
}