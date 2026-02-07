using System;
using System.Collections.Generic;

public interface IWorker
{
    int DoWork();
}

public class SimpleWorker : IWorker
{
    public SimpleWorker()
    {
    }

    public int DoWork()
    {
        return 5;
    }
}

public class SimpleWorker2 : IWorker
{
    public SimpleWorker2()
    {
    }

    public int DoWork()
    {
        return 6;
    }
}

class Program
{
    static int Test(Object worker)
    {
        return (worker as IWorker).DoWork();
    }

    static int Main()//string[] args)
    {
        Object[] objs = new Object[2];

        objs[0] = new SimpleWorker();
        objs[1] = new SimpleWorker2();

        int total = 0;
        for (int i = 0; i < objs.Length; i++)
        {
            total += Test(objs[i]);
        }
        return total;
    }
}
