class Program{
    static void Main(){
        int n = 18;
        long result = Fibonacci(n);
    }

    static long Fibonacci(int n){
        if (n <= 1) return n;

        long prev = 0, current = 1;

        for (int i = 2; i <= n; i++){
            long next = prev + current;
            prev = current;
            current = next;
        }

        return current;
    }
}