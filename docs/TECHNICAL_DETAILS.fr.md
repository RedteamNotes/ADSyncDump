# Analyse technique approfondie d'ADSyncDump
**Language**: [English](../TECHNICAL_DETAILS.md) | [中文](TECHNICAL_DETAILS.zh-CN.md) | **Français**

Ce document fournit une présentation complète et vérifiable des principes sous-jacents, des décisions de conception et des compromis d'implémentation d'ADSyncDump. Toutes les affirmations sont directement vérifiables par rapport au code source et aux mécanismes internes publics d'Azure AD Connect.

---

## 1. Contexte : Architecture d'Azure AD Connect
Azure AD Connect (AAD Connect) est l'outil de synchronisation d'annuaire officiel de Microsoft, déployé sur des serveurs joints à un domaine pour répliquer les objets Active Directory locaux vers Azure Active Directory. Par conception, il doit stocker deux jeux d'identifiants à privilèges élevés pour effectuer une synchronisation bidirectionnelle :
1. **Identifiants AD locaux** : Compte de service `MSOL_*` généré aléatoirement, disposant des droits `DS-Replication-Get-Changes` et `DS-Replication-Get-Changes-All` sur le domaine local (équivalent aux droits DCSync, capable d'extraire tous les hachages du domaine)
2. **Identifiants Azure AD** : Compte de service dans le locataire cible `.onmicrosoft.com`, disposant de rôles de synchronisation d'annuaire équivalents à Administrateur Global, capable de prendre le contrôle total du locataire Azure.

Composants principaux d'AAD Connect pertinents pour l'extraction d'identifiants :
| Composant | Détails |
|-----------|---------|
| Service de synchronisation | S'exécute en tant que compte de service virtuel `NT SERVICE\ADSync`, hébergé dans `miiserver.exe` |
| Stockage de configuration | SQL Server LocalDB (v1.x utilise l'instance `ADSync`, v2.x/v3.x utilise `ADSync2019`), stocke toutes les configurations de connecteur et les identifiants chiffrés dans la base de données `ADSync` |
| Bibliothèque de chiffrement | `mcrypt.dll` situé dans `C:\Program Files\Microsoft Azure AD Sync\Bin\`, implémente le chiffrement/déchiffrement des identifiants à l'aide de la Data Protection API (DPAPI) Windows |
| Stockage des clés | Les clés de chiffrement sont stockées dans la table `mms_server_configuration`, protégées par DPAPI liées au contexte de sécurité `NT SERVICE\ADSync` |

---

## 2. Principes cryptographiques de base
### 2.1 Modèle d'isolation DPAPI
La DPAPI (Data Protection API) Windows est la racine de confiance pour la protection des identifiants AAD Connect. Les blobs chiffrés DPAPI sont cryptographiquement liés au contexte de sécurité de l'utilisateur qui a chiffré les données :
- Un utilisateur (ou un principal de sécurité disposant d'un jeton identique) peut déchiffrer ses propres blobs DPAPI
- Le compte SYSTEM local ne peut pas déchiffrer les blobs DPAPI appartenant à d'autres comptes utilisateur, y compris les comptes de service virtuels
- Il n'existe pas de clé principale codée en dur ; le déchiffrement nécessite l'accès à la clé principale dérivée du mot de passe de l'utilisateur, protégée par le LSA système.

C'est la raison fondamentale pour laquelle l'exécution directe du code de déchiffrement en tant que SYSTEM échoue : même avec des droits d'administrateur complets, le processus n'a pas accès aux clés principales DPAPI du compte de service ADSync.

### 2.2 Flux de chiffrement ADSync
Le chiffrement des identifiants AAD Connect suit un flux fixe et publiquement vérifiable implémenté dans `mcrypt.dll` :
1. Au premier démarrage, KeyManager génère un jeu d'identifiants de clé en trois parties :
   - `keySetId` : ID de clé numérique (stocké sous forme d'entier non signé 32 bits)
   - `instanceId` : GUID unique à l'installation AAD Connect
   - `entropy` : GUID aléatoire ajouté comme entropie de chiffrement supplémentaire
2. Ces trois valeurs sont stockées en clair dans la table `mms_server_configuration`, mais la clé symétrique réelle utilisée pour déchiffrer les identifiants est chiffrée via DPAPI pour le compte `NT SERVICE\ADSync`.
3. Lors du déchiffrement des identifiants :
   - Appelez `KeyManager.LoadKeySet(entropy, instanceId, keySetId)` pour charger le matériel de clé chiffré DPAPI
   - Appelez `KeyManager.GetActiveCredentialKey()` pour initialiser le magasin de clés (étape d'initialisation non documentée requise)
   - Appelez `KeyManager.GetKey(1, out key)` pour récupérer la clé de déchiffrement des identifiants (l'ID de clé 1 est la clé d'identifiant universelle pour toutes les versions d'AAD Connect)
   - Appelez `key.DecryptBase64ToString(encryptedBlob, out plaintext)` pour déchiffrer le blob d'identifiant encodé en base64 en XML en clair.

### 2.3 Stockage des identifiants
Tous les identifiants de connecteur sont stockés dans la table `mms_management_agent` :
- `private_configuration_xml` : XML en clair contenant la configuration du connecteur (nom de domaine, nom d'utilisateur, ID de locataire, paramètres de connexion)
- `encrypted_configuration` : Blob chiffré encodé en base64 contenant des données sensibles (mots de passe, secrets)
- Chaque ligne représente un agent de gestion (connecteur) : AD local, Azure AD, LDAP, SQL, ADFS, etc.

---

## 3. Conception et justification de l'implémentation
Chaque décision de conception dans ADSyncDump est le résultat de tests itératifs sur des déploiements AAD Connect réels, résolvant des modes d'échec concrets observés dans les outils publics et les prototypes précoces.

### 3.1 Couche de connexion à la base de données
**Implémentation** : Le code tente automatiquement de se connecter aux deux instances `(localdb)\.\ADSync2019` et `(localdb)\.\ADSync`, en utilisant l'authentification Windows intégrée avec un délai de connexion de 5 secondes.
**Justification** :
- Le codage en dur d'une seule instance LocalDB échoue sur les différentes versions d'AAD Connect : v1.x utilise l'instance `ADSync`, tandis que v2.x (2019+) et v3.x ont renommé l'instance en `ADSync2019`. Le repli automatique garantit la compatibilité avec toutes les versions.
- L'authentification intégrée est utilisée car les administrateurs locaux (et SYSTEM) disposent par défaut d'un accès à la base LocalDB ADSync ; aucun identifiant supplémentaire n'est nécessaire.
- Un délai d'attente court de 5 secondes évite les blocages prolongés sur les machines sans AAD Connect installé, empêchant les gels longs dans les environnements C2.
- Aucune modification de base de données n'est effectuée ; toutes les requêtes sont en lecture seule pour éviter de laisser des traces d'audit.

### 3.2 Identification du processus de service
**Implémentation** : Au lieu d'énumérer tous les processus système pour trouver `miiserver.exe`, le code utilise l'API du Gestionnaire de contrôle de services (SCM) Windows pour interroger directement le PID en cours d'exécution du service `ADSync` via `QueryServiceStatusEx`.
**Justification** :
- L'énumération des noms de processus via `CreateToolhelp32Snapshot` n'est pas fiable dans tous les environnements :
  - Les processus 32 bits WOW64 ne parviennent pas à énumérer correctement les processus 64 bits
  - Certaines versions d'AAD Connect utilisent des noms de processus avec numéro de version
  - Dans certains environnements verrouillés, l'énumération de processus nécessite `SeDebugPrivilege`
- Les requêtes SCM sont fiables à 100% : le nom de service `ADSync` est fixe dans toutes les versions d'AAD Connect, et renvoie le PID exact du service en cours d'exécution quel que soit le nom du processus.
- Les requêtes SCM ne nécessitent aucun privilège spécial au-delà des droits d'administrateur local standard, et ne déclenchent pas de télémétrie de création/accès de processus.

### 3.3 Usurpation de jeton
**Implémentation** : Le code ouvre un handle vers le processus de service ADSync, duplique son jeton principal via `DuplicateToken`, et usurpe le jeton sur le thread actuel à l'aide de `WindowsIdentity.Impersonate()`. Une fois le déchiffrement terminé, le contexte d'usurpation est rétabli et tous les handles de jeton sont fermés.
**Justification** :
- L'usurpation de jeton est la seule méthode prise en charge pour accéder aux clés DPAPI du compte de service ADSync sans voler le mot de passe du compte ou créer un nouveau processus.
- `DuplicateToken` (et non `DuplicateTokenEx`) est utilisé pour créer un jeton de niveau usurpation, suffisant pour le déchiffrement DPAPI et le chargement de DLL, sans nécessiter les autorisations supplémentaires requises pour créer un jeton principal pour de nouveaux processus.
- L'usurpation est limitée au bloc de déchiffrement : après déchiffrement, `Undo()` est appelé dans un bloc `finally` pour rétablir le jeton de processus d'origine, conformément au principe du moindre privilège.
- Aucun nouveau processus n'est créé pendant l'usurpation, éliminant la télémétrie de processus fils et les problèmes d'isolation de session.

### 3.4 Déchiffrement en mémoire
**Implémentation** : Après avoir usurpé le jeton du service ADSync, le code définit le répertoire de travail actuel et le chemin de recherche DLL sur le répertoire Bin d'AAD Connect, puis charge `mcrypt.dll` directement dans le processus actuel par réflexion et appelle les méthodes KeyManager pour déchiffrer les identifiants en mémoire.
**Justification** :
- Cette approche remplace le modèle courant consistant à lancer un processus fils PowerShell pour effectuer le déchiffrement, qui présente plusieurs points d'échec critiques :
  1. Les processus fils PowerShell déclenchent l'analyse AMSI et la journalisation des blocs de script, détectées par tous les EDR modernes
  2. La redirection de sortie entre les processus parent et fils est sujette à des interblocages lorsque le tampon de sortie du processus fils est rempli
  3. L'isolation de session 0 et les profils utilisateur manquants pour le compte de service virtuel empêchent PowerShell de démarrer correctement
  4. Les processus fils créent une télémétrie de création de processus évidente (par exemple `notepad.exe → powershell.exe` est une règle de détection EDR prioritaire)
- `SetDllDirectory` et `Environment.CurrentDirectory` sont explicitement définis sur le répertoire Bin d'AAD Connect pour résoudre un échec critique du prototype précoce : `mcrypt.dll` charge les DLL dépendantes et les fichiers de configuration depuis son propre répertoire, et lèvera `FileNotFoundException` si le répertoire de travail du processus est défini sur le répertoire de travail C2.
- Le paramètre `keyId` est explicitement converti en `UInt32` : un prototype précoce a échoué car `KeyManager.LoadKeySet` attend un entier non signé 32 bits, tandis que la requête SQL renvoie un `Int32` signé ; cette incompatibilité de type n'est documentée nulle part et provoque un échec de déchiffrement silencieux.
- La méthode non documentée `GetActiveCredentialKey()` est appelée avant de récupérer la clé de déchiffrement : c'est une étape d'initialisation interne nécessaire pour déverrouiller le magasin de clés, et son omission provoque des exceptions de référence null lors de la récupération des clés.

### 3.5 Implémentation du contournement AMSI
**Implémentation** : Le contournement AMSI est désactivé par défaut, activé uniquement lorsque le paramètre `--bypass-amsi` est passé. Le contournement corrige le drapeau `amsiInitFailed` dans `System.Management.Automation.AmsiUtils` par réflexion, avec les noms de type et de champ divisés en tableaux de caractères pour éviter les signatures de chaînes statiques.
**Justification** :
- Rendre le contournement AMSI optionnel permet aux opérateurs de choisir en fonction de l'environnement cible : certains environnements surveillent les correctifs AMSI en mémoire, tandis que d'autres bloquent le chargement d'assemblys non corrigés.
- La division de chaînes évite la détection de signatures statiques : les chaînes complètes `AmsiUtils` et `amsiInitFailed` n'apparaissent jamais en tant que littéraux contigus dans le binaire, contournant les analyses de signatures statiques.
- Seul l'AMSI du processus actuel est corrigé ; aucune modification AMSI à l'échelle du système ou inter-processus n'est effectuée, réduisant la surface de persistance et de détection.
- Le correctif `amsiInitFailed` est le contournement AMSI le plus stable et le plus compatible sur toutes les versions de PowerShell/.NET, et ne nécessite pas de modifier les pages de mémoire exécutables (ce qui déclenche des alertes d'intégrité mémoire).

### 3.6 Compatibilité C# 5
**Implémentation** : Tout le code utilise uniquement la syntaxe C# 5.0, sans fonctionnalités C# 6+ (interpolation de chaînes, opérateurs conditionnels null, variables out, méthodes d'extension LINQ), et sans dépendances NuGet externes.
**Justification** :
- Le compilateur C# par défaut (`csc.exe`) inclus avec .NET Framework 4.8 (préinstallé sur tous les Windows 10/Server 2016+) ne prend en charge que C# 5.0. La restriction à la syntaxe C# 5 permet la compilation sur n'importe quel système Windows natif sans Visual Studio, Roslyn ou SDK supplémentaire.
- Tous les appels LINQ (par exemple `First()`) sont remplacés par des boucles `foreach` explicites pour éviter de nécessiter une référence à `System.Core.dll`, éliminant les problèmes de dépendances de compilation et d'exécution.
- Le binaire final ne dépend que de 4 assemblys .NET Framework intégrés présents sur toutes les versions Windows prises en charge : `mscorlib`, `System.Data`, `System.Xml`, `System.Security`. Aucune DLL supplémentaire n'est requise pour l'exécution.

### 3.7 Prise en charge multi-connecteurs
**Implémentation** : Le code énumère toutes les lignes de la table `mms_management_agent` au lieu de filtrer uniquement `ma_type='AD'`, identifie automatiquement les connecteurs AD locaux et Azure AD par type et nom, et renvoie les identifiants de tous les connecteurs pris en charge.
**Justification** :
- La plupart des outils publics d'extraction d'identifiants ADSync ne récupèrent que le compte `MSOL_*` AD local, ignorant l'identifiant de synchronisation Azure AD de plus grande valeur qui permet une prise de contrôle complète du locataire.
- La détection automatique de type élimine le besoin de configuration utilisateur : l'outil identifie et étiquette automatiquement les identifiants, y compris des notes sur les autorisations pour le contexte de l'opérateur.
- Le modèle d'énumération est extensible à d'autres types de connecteurs (LDAP, SQL, ADFS) dans les versions futures sans modifier la logique centrale.

---

## 4. Considérations de sécurité opérationnelle (OpSec)
Tous les choix de conception priorisent la minimisation de la surface de détection :
1. **Aucun processus fils créé** : Toutes les opérations se déroulent entièrement dans le processus sacrificiel C2. Aucun nouveau processus n'est créé à aucun moment, éliminant la télémétrie de création de processus et les règles de détection parent-enfant.
2. **Aucune utilisation de xp_cmdshell** : L'outil n'active ni n'utilise `xp_cmdshell` sur l'instance LocalDB, ce qui laisse des traces d'audit claires dans les journaux d'erreurs SQL Server et est surveillé par la plupart des outils de sécurité SQL. Toutes les opérations de base de données sont des requêtes SELECT en lecture seule.
3. **Aucune écriture sur disque** : Aucun fichier temporaire n'est écrit sur disque ; tous les déchiffrements s'effectuent en mémoire. L'outil ne modifie pas le registre, ne crée pas de services et ne modifie pas la configuration système.
4. **Utilisation minimale des handles** : Tous les handles Win32 (processus, jeton, gestionnaire de services) sont explicitement fermés immédiatement après utilisation pour éviter les fuites de handles et l'inspection des handles par les EDR.
5. **Aucun shellcode ou injection DLL réfléchie** : Tout le code s'exécute en tant que .NET managé, évitant les règles courantes de détection d'injection mémoire.

---

## 5. Problèmes historiques et résolutions
L'implémentation actuelle est le résultat de la résolution de modes d'échec concrets observés pendant le développement :
| Mode d'échec | Cause racine | Résolution |
|--------------|------------|------------|
| Le déchiffrement initial renvoie null/échec | L'exécution en tant que SYSTEM n'accorde pas l'accès aux clés principales DPAPI du compte de service ADSync | Implémenter l'usurpation de jeton du processus de service ADSync pour accéder au contexte DPAPI correct |
| L'énumération de processus ne trouve pas `miiserver.exe` | L'énumération des noms de processus n'est pas fiable sur les différentes versions et les environnements WOW64 | Utiliser l'API SCM pour interroger directement le PID du service ADSync par nom de service fixe |
| Le processus fils PowerShell se bloque indéfiniment | L'isolation de session 0, le blocage AMSI et les interblocages de tampon de sortie empêchent l'exécution du processus fils | Éliminer complètement les processus fils, effectuer le déchiffrement en mémoire par réflexion |
| KeyManager lève `InvalidCastException` | `LoadKeySet` attend un ID de clé `UInt32` non signé, tandis que la base de données renvoie un `Int32` signé | Convertir explicitement l'ID de clé en `uint` lors de l'appel des méthodes KeyManager |
| `mcrypt.dll` lève `FileNotFoundException` après chargement | La DLL recherche les fichiers de configuration dépendants dans le répertoire de travail du processus, qui est par défaut le répertoire de travail C2 | Définir explicitement le répertoire de travail et le chemin de recherche DLL sur le répertoire Bin d'AAD Connect |
| La compilation échoue avec csc système par défaut | L'utilisation de la syntaxe C# 6+ et de LINQ nécessite des références supplémentaires et des compilateurs plus récents | Restreindre le code à la syntaxe C# 5, remplacer LINQ par des boucles explicites, éliminer toutes les références externes |

---

## 6. Vérification et validation
Les identifiants extraits peuvent être vérifiés indépendamment comme valides :
1. **Identifiants AD locaux** : Le compte `MSOL_*` dispose toujours de droits de réplication d'annuaire. Validez avec `secretsdump.py -just-dc-user MSOL_<id> <DOMAINE>/<utilisateur_MSOL>:<mot_de_passe>@<controleur_domaine>` pour effectuer un DCSync.
2. **Identifiants Azure AD** : Le compte de synchronisation dispose de droits de synchronisation d'annuaire sur le locataire. Validez avec `Get-AADIntAccessTokenWithSyncCredentials` d'AADInternals pour récupérer un jeton d'accès Azure AD capable d'administrer le locataire.
3. L'outil ne produit pas de faux positifs : les échecs de déchiffrement sont explicitement signalés comme des erreurs, et aucun identifiant d'espace réservé ou invalide n'est renvoyé en cas d'échec.

---

## 7. Environnements pris en charge
- Azure AD Connect v1.x (2016), v2.x (2019/2022), v3.x (actuel)
- Windows Server 2016, 2019, 2022
- .NET Framework 4.5+ (préinstallé sur toutes les versions Windows prises en charge)
- Frameworks C2 : Sliver, Cobalt Strike, BruteRatel, Mythic et tous les frameworks prenant en charge `execute-assembly`

---

## 8. Questions fréquemment posées (FAQ)

### Q : Le chargement réflexif en mémoire via `execute-assembly` contourne-t-il automatiquement AMSI ?
**R : Non.** Le chargement réflexif en mémoire via `execute-assembly` ne contourne pas intrinsèquement AMSI, car l'analyse AMSI s'effectue au niveau du CLR (Runtime .NET), indépendamment du fait que l'assembly soit chargé depuis le disque ou la mémoire :
1. **Analyse AMSI intégrée au CLR** : Depuis .NET Framework 4.8 (runtime par défaut sur Windows 10 1903+ et Windows Server 2016+, requis pour toutes les versions modernes d'Azure AD Connect), le Common Language Runtime appelle `AmsiScanBuffer` pour analyser le code IL de *tous* les assemblys .NET lors du chargement, quelle que soit la source de chargement. Cette analyse s'effectue avant l'exécution du point d'entrée de l'assembly (méthode `Main`), et s'applique aussi bien aux assemblys chargés depuis le disque qu'aux assemblys chargés par réflexion via `Assembly.Load()` (mécanisme utilisé par toutes les implémentations de `execute-assembly`).
2. **Ce que `execute-assembly` contourne réellement** : L'exécution réflexive en mémoire ne permet d'échapper qu'à la détection de signatures statiques basée sur le disque (c'est-à-dire l'analyse des fichiers EXE écrits sur le disque). Elle n'interagit pas avec l'analyse AMSI au niveau du CLR et ne la désactive pas : AMSI inspecte directement les octets de l'assembly dans la mémoire du processus. Le lancement de processus sacrificiel (par ex. `-p notepad.exe`) ne sert qu'à isoler l'exécution pour éviter que le beacon ne soit corrompu si l'assembly plante ou est détecté ; il ne désactive pas AMSI dans le processus sacrificiel.
3. **Objectif du paramètre `--bypass-amsi`** :
   - ADSyncDump utilise l'obscurcissement de chaînes par découpage de caractères pour réduire la détection de signatures statiques lors du chargement initial de l'assembly, mais les produits EDR peuvent toujours signaler les appels d'API Win32 sensibles (usurpation de jeton, accès au SCM, routines de déchiffrement cryptographique) pendant l'exécution.
   - Le contournement AMSI inclus corrige le drapeau `amsiInitFailed` dans `System.Management.Automation.AmsiUtils` par réflexion au tout début de `Main`, faisant en sorte que toutes les demandes d'analyse AMSI ultérieures dans le processus actuel renvoient un résultat sain. Ce correctif s'exécute entièrement en mémoire et ne modifie pas les fichiers système sur le disque.
   - Remarque : Ce contournement n'échappe pas à l'analyse AMSI initiale avant l'exécution de l'assembly lui-même, mais neutralise la télémétrie et l'analyse AMSI pendant l'exécution de l'outil, ce qui est suffisant dans la plupart des scénarios d'équipe rouge.
4. **Cas limite d'environnement** : AMSI n'est pas intégré dans les versions de .NET Framework antérieures à 4.8 (par ex. Windows Server 2012 R2 et versions antérieures). Sur ces systèmes hérités, le paramètre `--bypass-amsi` est inutile et n'a aucun effet.
