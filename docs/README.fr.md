# ADSyncDump
**Language**: [English](../README.md) | [中文](README.zh-CN.md) | **Français**
<img align="right" src="../assets/ADSyncDump-Logo.png" alt="ADSyncDump Logo" width="220">

Extracteur d'identifiants en mémoire pour les serveurs Azure AD Connect. Extrait les identifiants de synchronisation Active Directory local et Azure Active Directory sans créer de processus fils.

<p>
  <img src="https://img.shields.io/badge/platform-Windows-blue" alt="Platform">
  <img src="https://img.shields.io/badge/language-C%23-239120" alt="Language">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License"></a>
</p>

<br clear="right">

## Fonctionnalités
- Extrait les identifiants du compte de synchronisation AD local (utilisateur MSOL_* avec droits DCSync/Réplication d'annuaire)
- Extrait les identifiants du compte de synchronisation Azure AD (droits équivalents Administrateur Global pour prise de contrôle du locataire Azure)
- Déchiffrement en mémoire via usurpation de jeton du service ADSync, sans PowerShell/processus fils
- Contournement AMSI optionnel (désactivé par défaut)
- Détection automatique des instances LocalDB, compatible avec toutes les versions d'AD Connect (v1/v2/v3)
- Localisation automatique du processus de service via le Gestionnaire de contrôle de services
- Journalisation d'exécution structurée
- Compatible C# 5, compilable avec csc.exe du système, aucune dépendance externe
- Fonctionne avec execute-assembly des C2 (Sliver, Cobalt Strike, etc.)

## Utilisation
```
# Par défaut (sans contournement AMSI)
execute-assembly ADSyncDump.exe -p notepad.exe

# Avec contournement AMSI
execute-assembly ADSyncDump.exe -p notepad.exe -- --bypass-amsi
```

Le chargement réflexif en mémoire via `execute-assembly` contourne-t-il automatiquement AMSI ? [Voir détails](TECHNICAL_DETAILS.fr.md#q-le-chargement-réflexif-en-mémoire-via-execute-assembly-contourne-t-il-automatiquement-amsi).

### Alias persistant Sliver C2
Vous pouvez enregistrer ADSyncDump comme alias persistant Sliver pour l'utiliser directement via la commande `adsyncdump` sur toutes les sessions, sans avoir à renvoyer le binaire à chaque fois.

1. Créez le répertoire de l'alias et déposez le binaire :
```
mkdir -p ~/.sliver-client/aliases/adsyncdump
cp ADSyncDump.exe ~/.sliver-client/aliases/adsyncdump/
```

2. Créez `alias.json` dans le même répertoire :
```json
{
  "name": "ADSyncDump",
  "version": "v0.8.1",
  "command_name": "adsyncdump",
  "original_author": "RedteamNotes",
  "repo_url": "https://github.com/RedteamNotes/ADSyncDump",
  "help": "Extrait les identifiants AD et Azure AD depuis les serveurs AD Connect",
  "long_help": "ADSyncDump extrait les identifiants de synchronisation AD local et Azure AD depuis les serveurs Azure AD Connect via usurpation de jeton de service en mémoire. Utilisez --bypass-amsi pour activer le contournement AMSI.",
  "entrypoint": "Main",
  "allow_args": true,
  "default_args": "",
  "is_reflective": false,
  "is_assembly": true,
  "files": [
    {
      "os": "windows",
      "arch": "amd64",
      "path": "ADSyncDump.exe"
    }
  ]
}
```

3. Chargez et vérifiez :
```
[sliver] > aliases load ~/.sliver-client/aliases/adsyncdump/alias.json
[*] ADSyncDump alias has been loaded

[sliver] (SESSION) > adsyncdump --bypass-amsi
```

Après chargement, `adsyncdump <arguments>` est équivalent à `execute-assembly ADSyncDump.exe -p notepad.exe <arguments>`. L'alias persiste après redémarrage du client Sliver. Mettez à jour en remplaçant le binaire dans le répertoire de l'alias.

### Paramètres
| Paramètre | Description |
|-----------|-------------|
| `--bypass-amsi` | Active le patch AMSI en mémoire |

## Sortie
Deux jeux d'identifiants sont renvoyés :
1. AD local : Compte MSOL_* avec droits de réplication d'annuaire, utilisable pour DCSync
2. Azure AD : Compte de service de synchronisation avec droits équivalents Administrateur Global, utilisable pour prise de contrôle du locataire Azure

## Compilation
### Prérequis
- **Windows uniquement** : La compilation nécessite le compilateur C# .NET Framework du système. La compilation Linux/MacOS n'est pas prise en charge (l'outil cible exclusivement Windows, dépend des API Win32 et de composants Windows uniquement comme LocalDB et mcrypt.dll).
- .NET Framework 4.8 (préinstallé sur Windows 10 1903+, Windows Server 2016+). Aucun Visual Studio ou SDK supplémentaire nécessaire.
- Doit être compilé en x64 : AD Connect s'exécute en tant que processus 64 bits, les builds 32 bits ne pourront pas accéder à LocalDB ni effectuer d'usurpation de jeton.

### Compilation en un clic
Exécutez `build.bat` directement sur Windows, le script localisera automatiquement csc.exe et générera un binaire x64 fonctionnel.

### Compilation manuelle
Exécutez la commande suivante dans l'invite de commandes ou PowerShell :
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /platform:x64 /target:exe /out:ADSyncDump.exe ADSyncDump.cs
```

### Notes de compilation
- Aucune dépendance externe ou paquet NuGet n'est nécessaire, tous les assemblys utilisés sont intégrés par défaut dans .NET Framework 4.x.
- Des binaires x64 précompilés sont disponibles dans les [Releases](https://github.com/RedteamNotes/ADSyncDump/releases), utilisables directement sans compilation.
- Ne compilez pas avec .NET Core/.NET 5+, le binaire doit cibler .NET Framework 4.x pour s'exécuter sur les serveurs AD Connect sans dépendances d'exécution supplémentaires.

## Notes
- Nécessite des droits administrateur local sur le serveur AD Connect
- Tous les déchiffrements s'effectuent en mémoire, aucun processus fils n'est créé
- Testé sur Windows Server 2016/2019/2022 avec AD Connect v1/v2/v3

## Historique des versions
### v0.8.1
- Première version publique
- Extraction des identifiants AD local + Azure AD
- Déchiffrement en mémoire par usurpation de jeton
- Contournement AMSI optionnel
- Détection automatique des instances de base de données et du PID du service

## Documentation supplémentaire
- [Détails techniques approfondis](TECHNICAL_DETAILS.fr.md) - Présentation complète de l'architecture sous-jacente, principes cryptographiques, décisions d'implémentation, considérations OpSec et résolution des pannes.
