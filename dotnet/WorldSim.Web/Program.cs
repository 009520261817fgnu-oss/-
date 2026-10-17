using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// No Razor components: the page is plain HTML + Three.js that calls GameApi through JS interop.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
await builder.Build().RunAsync();
