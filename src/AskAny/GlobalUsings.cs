global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Threading;
global using System.Threading.Tasks;

// 让测试工程能验证 internal 的纯函数（例如模型列表地址推导、返回体解析），
// 又不把这些细节暴露成公开 API。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AskAny.Tests")]
