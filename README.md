# Kubism

**Kub**ernetes **I**nterface for **S**upervising **M**ulti-clusters. *Inspect. Steer. Monitor.*
Cross-platform (macOS, Linux, Windows) desktop app to explore and operate several Kubernetes clusters at once. Avalonia 12 on .NET 10. See [CLAUDE.md](CLAUDE.md) and [tasks.md](tasks.md).

```bash
dotnet build Kubism.slnx
dotnet test Kubism.slnx
dotnet run --project Kubism.Desktop -- --demo   # fake API server, no cluster needed
```
