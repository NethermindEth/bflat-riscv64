// A run that returns non-zero must not end as a success: the platform writes
// the cycle CSR, which the simulator rejects as no circuit satisfies it.
class Program
{
    static int Main() => 3;
}
