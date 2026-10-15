using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor.AssemblyPostProcess
{
    [InitializeOnLoad]
    public static class AddInternalsVisibleToForAllUserAssembliesPostProcess
    {
        public static readonly DirectoryInfo AdjustedAssemblyRoot;

        private static readonly Assembly CecilAssembly;
        private static readonly Type AssemblyDefinitionType;
        private static readonly Type ReaderParametersType;
        private static readonly Type CustomAttributeType;
        private static readonly Type CustomAttributeArgumentType;

        static AddInternalsVisibleToForAllUserAssembliesPostProcess()
        {
            AdjustedAssemblyRoot = new DirectoryInfo(Path.Combine(Application.dataPath, "..", "Temp", "Fast Script Reload", "AdjustedDlls"));

            CecilAssembly = typeof(HarmonyLib.Harmony).Assembly;
            AssemblyDefinitionType = CecilAssembly.GetType("Mono.Cecil.AssemblyDefinition");
            ReaderParametersType = CecilAssembly.GetType("Mono.Cecil.ReaderParameters");
            CustomAttributeType = CecilAssembly.GetType("Mono.Cecil.CustomAttribute");
            CustomAttributeArgumentType = CecilAssembly.GetType("Mono.Cecil.CustomAttributeArgument");
        }

        /// <param name="typesToOpen">
        ///     Top-level types, by CLR full name, whose nested types and private members become internal in the copy: a patch of
        ///     such a type names its original nested types, which it could not reach while they stay private to another type.
        /// </param>
        public static string CreateAssemblyWithInternalsContentsVisibleTo(Assembly changedAssembly, string visibleToAssemblyName, ICollection<string> typesToOpen = null)
        {
            if (!AdjustedAssemblyRoot.Exists)
                AdjustedAssemblyRoot.Create();

            // var readerParams = new ReaderParameters { ReadWrite = false };
            var readerParams = Activator.CreateInstance(ReaderParametersType);
            ReaderParametersType.GetProperty("ReadWrite").SetValue(readerParams, false);

            // var assembly = AssemblyDefinition.ReadAssembly(path, readerParams);
            var readAssemblyMethod = AssemblyDefinitionType.GetMethod("ReadAssembly", new[] { typeof(string), ReaderParametersType });
            var assemblyDef = readAssemblyMethod.Invoke(null, new[] { changedAssembly.Location, readerParams });

            try
            {
                // var mainModule = assembly.MainModule;
                var mainModule = AssemblyDefinitionType.GetProperty("MainModule").GetValue(assemblyDef);
                var moduleDefinitionType = mainModule.GetType();

                // var attributeCtor = mainModule.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor(...));
                var importRefMethod = moduleDefinitionType.GetMethod("ImportReference", new[] { typeof(MethodBase) });
                var attrCtorInfo = typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) });
                var attributeCtor = importRefMethod.Invoke(mainModule, new object[] { attrCtorInfo });

                // var attribute = new CustomAttribute(attributeCtor);
                var attribute = Activator.CreateInstance(CustomAttributeType, attributeCtor);

                // var typeSystem = mainModule.TypeSystem;
                var typeSystem = moduleDefinitionType.GetProperty("TypeSystem").GetValue(mainModule);
                var stringTypeRef = typeSystem.GetType().GetProperty("String").GetValue(typeSystem);

                // attribute.ConstructorArguments.Add(new CustomAttributeArgument(mainModule.TypeSystem.String, visibleToAssemblyName));
                var ctorArg = Activator.CreateInstance(CustomAttributeArgumentType, stringTypeRef, visibleToAssemblyName);
                var ctorArgs = (IList)CustomAttributeType.GetProperty("ConstructorArguments").GetValue(attribute);
                ctorArgs.GetType().GetMethod("Add").Invoke(ctorArgs, new[] { ctorArg });

                // assembly.CustomAttributes.Add(attribute);
                var customAttrs = (IList)AssemblyDefinitionType.GetProperty("CustomAttributes").GetValue(assemblyDef);
                customAttrs.GetType().GetMethod("Add").Invoke(customAttrs, new[] { attribute });

                if (typesToOpen != null && typesToOpen.Count > 0)
                {
                    foreach (var type in (IEnumerable)moduleDefinitionType.GetProperty("Types").GetValue(mainModule))
                    {
                        if (typesToOpen.Contains((string)type.GetType().GetProperty("FullName").GetValue(type))) OpenUp(type);
                    }
                }

                // var newAssemblyPath = ...;
                var assemblyName = AssemblyDefinitionType.GetProperty("Name").GetValue(assemblyDef);
                var assemblyNameStr = (string)assemblyName.GetType().GetProperty("Name").GetValue(assemblyName);
                var newAssemblyPath = new FileInfo(Path.Combine(AdjustedAssemblyRoot.FullName, assemblyNameStr) + ".dll").FullName;

                // assembly.Write(newAssemblyPath);
                AssemblyDefinitionType.GetMethod("Write", new[] { typeof(string) }).Invoke(assemblyDef, new object[] { newAssemblyPath });

                return newAssemblyPath;
            }
            finally
            {
                // AssemblyDefinition is IDisposable
                if (assemblyDef is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        // Access bits as ECMA-335 numbers them, the same for Mono.Cecil's own attribute enums.
        private const uint AccessMask = 7, NestedPrivate = 3, NestedFamily = 4, NestedAssembly = 5, NestedFamAndAssem = 6, NestedFamOrAssem = 7;
        private const uint MemberPrivate = 1, MemberFamAndAssem = 2, MemberAssembly = 3, MemberFamily = 4, MemberFamOrAssem = 5;

        /// <summary>
        ///     Makes <paramref name="type"/>'s private members and private nested types internal, and protected ones protected
        ///     internal, through all its nested types except those the compiler generated: those are never named in source.
        /// </summary>
        private static void OpenUp(object type)
        {
            Widen(type, "Attributes", new[] { (NestedPrivate, NestedAssembly), (NestedFamAndAssem, NestedAssembly), (NestedFamily, NestedFamOrAssem) });
            foreach (var collection in new[] { "Methods", "Fields" })
            {
                foreach (var member in (IEnumerable)type.GetType().GetProperty(collection).GetValue(type))
                {
                    Widen(member, "Attributes", new[] { (MemberPrivate, MemberAssembly), (MemberFamAndAssem, MemberAssembly), (MemberFamily, MemberFamOrAssem) });
                }
            }
            foreach (var nested in (IEnumerable)type.GetType().GetProperty("NestedTypes").GetValue(type))
            {
                if (((string)nested.GetType().GetProperty("Name").GetValue(nested)).StartsWith("<")) continue;
                OpenUp(nested);
            }
        }

        private static void Widen(object definition, string attributesProperty, (uint narrow, uint wide)[] mapping)
        {
            var property = definition.GetType().GetProperty(attributesProperty);
            var attributes = Convert.ToUInt32(property.GetValue(definition));
            foreach (var (narrow, wide) in mapping)
            {
                if ((attributes & AccessMask) != narrow) continue;
                property.SetValue(definition, Enum.ToObject(property.PropertyType, (attributes & ~AccessMask) | wide));
                return;
            }
        }
    }
}