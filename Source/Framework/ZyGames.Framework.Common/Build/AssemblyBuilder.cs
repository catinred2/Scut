/****************************************************************************
Copyright (c) 2013-2015 scutgame.com

http://www.scutgame.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
****************************************************************************/

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using System.Security.Policy;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ZyGames.Framework.Common.Build
{
    /// <summary>
    /// Assemblly builder.
    /// </summary>
    public static class AssemblyBuilder
    {

        /// <summary>
        /// Read assembly.
        /// </summary>
        public static Assembly ReadAssembly(string assemblyPath, Evidence evidence)
        {
            FileStream fileStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return ReadAssembly(fileStream, evidence);
            }
            finally
            {
                fileStream.Close();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="evidence"></param>
        /// <returns></returns>
        public static Assembly ReadAssembly(Stream stream, Evidence evidence)
        {
            SecurityPermission securityPermission = new SecurityPermission(SecurityPermissionFlag.ControlEvidence);
            securityPermission.Assert();
            int num = (int)stream.Length;
            byte[] array = new byte[num];
            stream.Read(array, 0, num);
            try
            {
                return Assembly.Load(array, null, evidence);
            }
            finally
            {
                CodeAccessPermission.RevertAssert();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="assemblyPath"></param>
        /// <returns></returns>
        public static Stream ReadAssembly(string assemblyPath)
        {
            FileStream fileStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                int num = (int)fileStream.Length;
                byte[] array = new byte[num];
                fileStream.Read(array, 0, num);
                return new MemoryStream(array);
            }
            finally
            {
                fileStream.Close();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="assemblyPath"></param>
        /// <param name="outputAssembly"></param>
        /// <returns></returns>
        public static bool BuildToStream(string assemblyPath, out Stream outputAssembly)
        {
            bool setSuccess = false;
            string currentPath = Path.GetDirectoryName(assemblyPath);
            outputAssembly = new MemoryStream();
            using (Stream stream = ReadAssembly(assemblyPath))
            {
                var ass = AssemblyDefinition.ReadAssembly(stream);
                var types = ass.MainModule.Types.Where(p => !p.IsEnum).ToList();
                foreach (TypeDefinition type in types)
                {
                    setSuccess = ProcessEntityType(type, setSuccess, currentPath);
                }
                if (setSuccess)
                {
                    ass.Write(outputAssembly);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="assemblyPath"></param>
        /// <param name="currentPath"></param>
        /// <param name="savePath"></param>
        /// <returns></returns>
        public static bool BuildToFile(string assemblyPath, string currentPath, string savePath = null)
        {
            bool setSuccess = false;
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = assemblyPath;
            }
            FileStream fileStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] array;
            try
            {
                int num = (int)fileStream.Length;
                array = new byte[num];
                fileStream.Read(array, 0, num);
            }
            finally
            {
                fileStream.Close();
            }

            using (MemoryStream stream = new MemoryStream(array))
            {
                var ass = AssemblyDefinition.ReadAssembly(stream);
                var types = ass.MainModule.Types.Where(p => !p.IsEnum).ToList();
                foreach (TypeDefinition type in types)
                {
                    setSuccess = ProcessEntityType(type, setSuccess, currentPath);
                }

                if (setSuccess)
                {
                    ass.Write(savePath);
                    return true;
                }
            }
            return false;
        }


        private static bool ProcessEntityType(TypeDefinition type, bool setSuccess, string currentPath)
        {
            TypeDefinition baseType = FindBaseTypeDefinition(type.BaseType, "AbstractEntity", currentPath);
            if (baseType != null)
            {
                foreach (PropertyDefinition prop in type.Properties)
                {
                    foreach (CustomAttribute attribute in prop.CustomAttributes)
                    {
                        if (attribute.Constructor != null &&
                            attribute.Constructor.DeclaringType != null &&
                            attribute.Constructor.DeclaringType.Name == "EntityFieldAttribute")
                        {
                            setSuccess = SetChangePropertyMethod(type, baseType, prop, setSuccess);
                        }
                    }
                }
                return setSuccess;
            }

            baseType = FindBaseTypeDefinition(type.BaseType, "EntityChangeEvent", currentPath);
            if (baseType != null)
            {
                //子类定义模式
                foreach (PropertyDefinition prop in type.Properties)
                {
                    setSuccess = SetChildNotifyMethod(type, baseType, prop, setSuccess);
                }
            }
            return setSuccess;
        }

        private static bool SetChildNotifyMethod(TypeDefinition type, TypeDefinition baseType, PropertyDefinition prop, bool setSuccess)
        {
            MethodDefinition notifyMethod = baseType.Methods.First(m => m.Name == "BindAndNotify");
            if (notifyMethod == null)
            {
                return setSuccess;
            }
            if (prop.SetMethod == null)
            {
                return setSuccess;
            }
            WriteAopCode(type, prop, notifyMethod, prop.Name);
            return true;
        }

        private static bool SetChangePropertyMethod(TypeDefinition type, TypeDefinition baseType, PropertyDefinition prop, bool setSuccess)
        {
            MethodDefinition notifyMethod = baseType.Methods.First(m => m.Name == "BindAndChangeProperty");
            if (notifyMethod == null || prop.SetMethod == null)
            {
                return setSuccess;
            }
            WriteAopCode(type, prop, notifyMethod, prop.Name);
            return true;
        }

        private static void WriteAopCode(TypeDefinition type, PropertyDefinition prop, MethodDefinition method, string propName)
        {
            try
            {

                MethodDefinition setMethod = prop.SetMethod;
                if (setMethod.Body.Instructions.Count > 5)
                {
                    return;
                }
                var field = type.Fields.FirstOrDefault(p => p.Name.StartsWith("<" + propName + ">") ||
                    p.Name.Equals("_" + propName, StringComparison.CurrentCultureIgnoreCase));
                if (field == null)
                {
                    return;
                }
                while (setMethod.Body.Instructions.Count > 1)
                {
                    setMethod.Body.Instructions.RemoveAt(0);
                }
                MethodReference notifyMethod = type.Module.Import(method);
                var paramType = setMethod.Parameters[0].ParameterType;
                //bool isIgnore = paramType.Name == "DateTime" || paramType.Name == "Boolean";
                //var fieldRefType = Type.GetType("System.Object&");
                //var fieldType = Type.GetType("System.Object");
                //var exchangeMethod = type.Module.Import(typeof(Interlocked).GetMethod("Exchange", new Type[] { fieldRefType, fieldType }));
                ILProcessor worker = setMethod.Body.GetILProcessor();
                Instruction ins = setMethod.Body.Instructions[setMethod.Body.Instructions.Count - 1];
                var equalsMethod = type.Module.Import(typeof(Object).GetMethod("Equals", new Type[] { typeof(object), typeof(object) }));
                setMethod.Body.Variables.Add(new VariableDefinition(equalsMethod.ReturnType));
                worker.InsertBefore(setMethod.Body.Instructions[0], worker.Create(OpCodes.Nop));

                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_0));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldfld, field));
                worker.InsertBefore(ins, worker.Create(OpCodes.Box, paramType));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_1));
                worker.InsertBefore(ins, worker.Create(OpCodes.Box, paramType));
                worker.InsertBefore(ins, worker.Create(OpCodes.Call, equalsMethod));

                worker.InsertBefore(ins, worker.Create(OpCodes.Stloc_0));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldloc_0));
                worker.InsertBefore(ins, worker.Create(OpCodes.Brtrue_S, ins));

                //exchange field value
                worker.InsertBefore(ins, worker.Create(OpCodes.Nop));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_0));

                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_1));
                worker.InsertBefore(ins, worker.Create(OpCodes.Stfld, field));

                //else
                //{
                //    worker.InsertBefore(ins, worker.Create(OpCodes.Ldflda, field));
                //    worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_1));
                //    worker.InsertBefore(ins, worker.Create(OpCodes.Call, exchangeMethod));
                //    worker.InsertBefore(ins, worker.Create(OpCodes.Pop));
                //}

                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_0));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldarg_0));
                worker.InsertBefore(ins, worker.Create(OpCodes.Ldfld, field));
                worker.InsertBefore(ins, worker.Create(OpCodes.Box, paramType));
                if (notifyMethod.Parameters.Count == 2)
                {
                    worker.InsertBefore(ins, worker.Create(OpCodes.Ldstr, propName));
                }
                worker.InsertBefore(ins, worker.Create(OpCodes.Call, notifyMethod));

                worker.InsertBefore(ins, worker.Create(OpCodes.Nop));
                worker.InsertBefore(ins, worker.Create(OpCodes.Nop));
            }
            catch (Exception ex)
            {
                throw new Exception(string.Format("at type:{0},propName:{1}", type.FullName, propName), ex);
            }
        }


        private static TypeDefinition FindBaseTypeDefinition(TypeReference type, string name, string currentPath)
        {
            if (type == null ||
                type.Module == null ||
                type.Name == typeof(object).Name)
            {
                return null;
            }
            TypeDefinition typeDefinition = null;

            string fileName = Path.Combine(currentPath, type.Scope.Name);
            if (!fileName.EndsWith(".dll", StringComparison.CurrentCultureIgnoreCase))
            {
                fileName += ".dll";
            }
            var md = ModuleDefinition.ReadModule(fileName);
            if (md != null && md.Assembly != null)
            {
                AssemblyDefinition ad = md.Assembly;
                typeDefinition = ad.MainModule.GetType(type.Namespace, type.Name);
            }
            if (typeDefinition != null)
            {
                typeDefinition = typeDefinition.Resolve();
                if (string.Equals(typeDefinition.Name, name, StringComparison.CurrentCultureIgnoreCase))
                {
                    return typeDefinition;
                }

                if (typeDefinition.BaseType != null)
                {
                    return FindBaseTypeDefinition(typeDefinition.BaseType, name, currentPath);
                }
            }
            return typeDefinition;
        }
    }
}