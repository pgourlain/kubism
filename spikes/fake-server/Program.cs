using Kubism.Testing;

// Starts the fake API server, prints the kubeconfig path on the first line and runs until stdin closes.
await using var server = await FakeApiServer.StartAsync();
Console.WriteLine(server.WriteKubeconfig());
Console.Out.Flush();
await Task.Run(() => { while (Console.In.ReadLine() is not null) { } });
