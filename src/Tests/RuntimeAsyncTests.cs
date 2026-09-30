#if NET11_0_OR_GREATER
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PDFtoImage.Parallel;
using PDFtoImage.Parallel.Internals;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PDFtoImage.Tests
{
    [TestClass]
    public sealed class RuntimeAsyncTests
    {
        [TestMethod]
        public void TaskMethodsUseRuntimeAsync()
        {
            AssertRuntimeAsync(typeof(ParallelPdfProcessor).GetMethod(nameof(ParallelPdfProcessor.ToImageAsync))!);
            AssertRuntimeAsync(typeof(ParallelPdfProcessor).GetMethod(nameof(ParallelPdfProcessor.DisposeAsync))!);
            AssertRuntimeAsync(typeof(WorkerProtocol).GetMethod(nameof(WorkerProtocol.ReadMessageAsync), BindingFlags.NonPublic | BindingFlags.Static)!);

            var runtimeAsyncCount = typeof(ParallelPdfProcessor).Assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Count(method => method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async));

            Assert.IsTrue(runtimeAsyncCount >= 20, "Expected runtime-async methods, found " + runtimeAsyncCount + ".");
        }

        [TestMethod]
        public void AsyncIteratorsStayOnCompilerStateMachines()
        {
            // IAsyncEnumerable methods are not lowered to runtime-async by this compiler.
            foreach (var method in typeof(Conversion).GetMethods().Where(method => method.Name == nameof(Conversion.ToImagesAsync)))
                AssertStateMachine(method);

            var iterator = typeof(ParallelPdfProcessor).GetMethod(
                "ToImagesFromStreamAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(iterator);
            AssertStateMachine(iterator);
        }

        private static void AssertRuntimeAsync(MethodInfo method)
        {
            Assert.IsTrue(
                method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async),
                method.Name + " is still a compiler state machine.");
            Assert.IsFalse(HasStateMachineType(method), method.Name + " still has a compiler-generated state machine.");
        }

        private static void AssertStateMachine(MethodInfo method)
        {
            Assert.IsFalse(method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async), method.Name);
            Assert.IsTrue(HasStateMachineType(method), method.Name + " has no compiler-generated state machine.");
        }

        private static bool HasStateMachineType(MethodInfo method)
        {
            var prefix = "<" + method.Name + ">";
            return method.DeclaringType!.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Any(type => type.Name.StartsWith(prefix, System.StringComparison.Ordinal));
        }
    }
}
#endif
