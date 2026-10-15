using System.Collections;
using FastScriptReload.Tests.Runtime.Integration.CodePatterns;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.TestTools;

namespace FastScriptReload.Tests.Editor.Integration.CodePatterns
{
    public class MethodUsingNestedTypesTest : CompileWithRedirectTestBase
    {
        [UnityTest]
        public IEnumerator PatchedMethod_CreatesNestedInstances_TheyAreTheOriginalTypes()
        {
            var instance = new GameObject("instance").AddComponent<MethodUsingNestedTypes>();

            TestCompileAndDetour(ResolveFullTestFilePath(@"Runtime\Integration\CodePatterns\MethodUsingNestedTypes.cs"), compilationResult => instance.UseNestedTypes());

            AssertDetourConfirmed(typeof(MethodUsingNestedTypes), nameof(instance.UseNestedTypes), result => (bool)result,
                "a nested instance the patch creates should be the same type as one the original code created");
            yield return null;
        }

        [UnityTest]
        public IEnumerator NestedClassMethod_Changed_RedirectedOnOriginalInstance()
        {
            var instance = new GameObject("instance").AddComponent<MethodUsingNestedTypes>();
            Assert.AreEqual(2, instance.KeptTwice());

            var after = 0;
            TestCompileAndDetour(ResolveFullTestFilePath(@"Runtime\Integration\CodePatterns\MethodUsingNestedTypes.cs"), compilationResult => after = instance.KeptTwice());

            Assert.AreEqual(3, after, "the nested class's changed method should run on an instance the original code created");
            yield return null;
        }
    }
}
