using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        var path = @"C:\Users\macie\.nuget\packages\nefarius.vigem.client\1.19.197\lib\net6.0\Nefarius.ViGEm.Client.dll";
        try
        {
            var asm = Assembly.LoadFrom(path);
            Console.WriteLine("Loaded: " + asm.FullName);
            foreach (var t in asm.GetTypes()) Console.WriteLine(t.FullName);
            Console.WriteLine("\n--- Methods for IXbox360Controller ---");
            var ixType = asm.GetType("Nefarius.ViGEm.Client.Targets.IXbox360Controller");
            if (ixType != null)
            {
                foreach (var m in ixType.GetMethods()) Console.WriteLine(m.ToString());
                foreach (var p in ixType.GetProperties()) Console.WriteLine("PROP: " + p.Name + " -> " + p.PropertyType.FullName);
            }
            Console.WriteLine("\n--- Methods for Xbox360Controller ---");
            var ttype = asm.GetType("Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Controller");
            if (ttype != null)
            {
                foreach (var m in ttype.GetMethods()) Console.WriteLine(m.ToString());
                foreach (var p in ttype.GetProperties()) Console.WriteLine("PROP: " + p.Name + " -> " + p.PropertyType.FullName);
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.WriteLine("LoaderExceptions:");
            foreach (var le in ex.LoaderExceptions) Console.WriteLine(le.Message);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
        }
    }
}
