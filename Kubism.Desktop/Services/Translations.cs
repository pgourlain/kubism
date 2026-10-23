namespace Kubism.Desktop.Services;

/// <summary>Minimal localization: English first, French second. Unknown keys fall back to the English text itself.</summary>
public static class Translations
{
    private static readonly Dictionary<string, string> Fr = new()
    {
        ["Clusters"] = "Clusters", ["Add kubeconfig…"] = "Ajouter un kubeconfig…", ["Add folder…"] = "Ajouter un dossier…",
        ["Connect"] = "Se connecter", ["Disconnect"] = "Se déconnecter", ["Overview"] = "Vue d'ensemble", ["Nodes"] = "Nœuds",
        ["Workloads"] = "Charges de travail", ["Config"] = "Configuration", ["Network"] = "Réseau", ["Storage"] = "Stockage",
        ["Namespaces"] = "Namespaces", ["Events"] = "Évènements", ["Helm"] = "Helm", ["Access Control"] = "Contrôle d'accès",
        ["Custom Resources"] = "Ressources personnalisées", ["All kinds"] = "Tous les types", ["Right-sizing"] = "Dimensionnement",
        ["Activity"] = "Activité", ["Port forwards"] = "Redirections de ports", ["Filter"] = "Filtrer", ["Refresh"] = "Actualiser",
        ["Delete"] = "Supprimer", ["Edit"] = "Modifier", ["Save"] = "Enregistrer", ["Cancel"] = "Annuler", ["Logs"] = "Journaux",
        ["Shell"] = "Terminal", ["YAML"] = "YAML", ["Related"] = "Associés", ["Live"] = "En direct", ["Reconnecting…"] = "Reconnexion…",
        ["Scale"] = "Mettre à l'échelle", ["Restart"] = "Redémarrer", ["Cordon"] = "Isoler", ["Uncordon"] = "Rétablir", ["Drain"] = "Vider",
        ["File"] = "Fichier", ["View"] = "Affichage", ["Help"] = "Aide", ["Quit"] = "Quitter", ["About Kubism"] = "À propos de Kubism",
        ["Welcome"] = "Bienvenue", ["No clusters found"] = "Aucun cluster trouvé", ["Command palette"] = "Palette de commandes",
        ["Production"] = "Production", ["Read-only"] = "Lecture seule", ["Normal"] = "Normal", ["Apply"] = "Appliquer",
        ["Search"] = "Rechercher", ["Pause"] = "Pause", ["Resume"] = "Reprendre", ["Clear"] = "Effacer", ["Download"] = "Télécharger",
        ["Export"] = "Exporter", ["Copy"] = "Copier", ["Cut"] = "Couper", ["Paste"] = "Coller", ["Select all"] = "Tout sélectionner", ["Open Log Folder"] = "Ouvrir le dossier des journaux", ["Copy diagnostics"] = "Copier les diagnostics",
        ["Diagnostics"] = "Diagnostics", ["RBAC graph"] = "Graphe RBAC", ["Network graph"] = "Graphe réseau", ["Releases"] = "Releases", ["Namespace"] = "Namespace", ["All namespaces"] = "Tous les namespaces",
    };

    public static string Language { get; set; } = "en";

    public static string GetString(string key) =>
        Language.StartsWith("fr", StringComparison.OrdinalIgnoreCase) && Fr.TryGetValue(key, out var v) ? v : key;

    public static string T(this string key) => GetString(key);

    public static IReadOnlyDictionary<string, string> French => Fr;
}
