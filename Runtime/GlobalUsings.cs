// Equivalent of <ImplicitUsings>enable</ImplicitUsings> for Unity.
// These match the default implicit usings from Microsoft.NET.Sdk.
// Requires C# 10+ — skip on Unity versions older than 2022.2.

#if !UNITY_2017_1_OR_NEWER
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
#endif
