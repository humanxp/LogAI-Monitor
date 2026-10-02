// Prints the public surface of the scrypt package, so password GENERATION can use
// the real API instead of a guessed one. Verification only ever needed the
// compare path; creating a user needs derivation.

using System.Reflection;

namespace LogAI.Web;

internal static class ScryptApiDump
{
    public static int Run()
    {
        var assembly = Assembly.Load("Norgerman.Cryptography.Scrypt");
        Console.WriteLine("Assembly: " + assembly.FullName);
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName))
        {
            Console.WriteLine("\n== " + type.FullName + (type.IsEnum ? " (enum)" : ""));
            if (type.IsEnum)
            {
                Console.WriteLine("   values: " + string.Join(", ", Enum.GetNames(type)));
                continue;
            }
            foreach (var ctor in type.GetConstructors())
                Console.WriteLine("   ctor(" + string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   " + (method.IsStatic ? "static " : "") + method.ReturnType.Name + " "
                    + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   prop " + property.PropertyType.Name + " " + property.Name);
        }
        return 0;
    }
}
