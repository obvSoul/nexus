# Performances mesurées

Toutes les valeurs viennent d'exécutions réelles, pas d'estimations.

**Machine de référence** : Windows 11 Famille 10.0.26200, AMD Ryzen 9 8945HX (16 cœurs / 32 threads), 32 Go de RAM, SSD.
**Index de référence** : le disque C: réel de cette machine, environ 1,1 million de fichiers et 224 000 dossiers
(base SQLite de 768 Mo). Recherche : index synthétique de 1 million de lignes.
**Build** : Release, autonome, x64, ReadyToRun.

## Résultats

| Mesure | Objectif | Avant la Phase 8 | Après |
|---|---|---|---|
| Ouverture de la fenêtre (médiane de 5, 1er lancement exclu) | < 2 s | 3,6 s | **0,58 s** |
| Premier lancement à froid | - | - | 1,26 s |
| RAM au repos (Working Set / mémoire privée, index de 1,1 M chargé) | minimale | 257 / 199 Mo | **63 / 105 Mo** |
| CPU au repos | minimal | 31 % (voir note) | **1,2 % d'un cœur** |
| Premier scan de C: | - | 199 s | **163 s** |
| Pic mémoire pendant le premier scan | - | 116 Mo | 155 Mo |
| Rescan incrémental (sans modification notable) | pas de scan complet | 21 à 42 s | 21 à 61 s (voir note) |
| Première page de résultats (requêtes courantes) | < 100 ms | 0,3 à 30 ms | 0,1 à 28 ms |
| Tri par taille sur 133 000 résultats | < 100 ms | 144 ms | 147 ms |
| Expression régulière (parcours complet de 1 M) | - | 338 ms | 354 ms |
| Calcul des tailles de dossiers (224 000 dossiers) | - | 4,0 s | 4,3 s |
| Top 200 des fichiers de C: | - | 494 ms | **0,4 ms** |
| Analyse des applications (dans l'app) | - | 24 s | 4 à 5 s |
| Doublons sur C: (≥ 1 Mo, 4,57 Go lus) | - | - | 13,6 s, puis 0,4 s avec le cache de hash |

## Notes d'interprétation

- **Objectifs non atteints** : deux requêtes dépassent 100 ms, le tri par taille sur un très grand nombre de résultats et les
  expressions régulières, qui doivent examiner tous les noms. Les résultats restent annulables et n'ont aucun effet sur l'interface.
- **CPU au repos** : la mesure d'origine (31 %) a été prise avec un client UI Automation connecté à la fenêtre, ce qui peut la
  fausser, donc elle n'est pas directement comparable. La cause exacte de ce pic n'a pas été isolée : une mesure sans ce client,
  juste après un scan, a encore montré environ 14 % sur 10 s, puis les mesures suivantes ont donné 0,2 à 1,2 %.
  Le 1,2 % inclut l'activité réelle du disque C: suivie par la surveillance en temps réel.
- **Rescan** : très variable selon l'activité du PC, car il tourne en priorité basse et ralentit dès que d'autres programmes
  travaillent. Il n'a pas été accéléré dans cette phase.
- **Mémoire au repos** : le Working Set est réduit après chaque travail. Il remonte pendant une analyse.

## Ce qui a changé

- Démarrage : compilation ReadyToRun ; un arrêt normal n'était pas reconnu comme tel si l'indexeur était en cours, ce qui
  déclenchait une vérification complète de la base à chaque lancement.
- Mémoire : ramasse-miettes sans thread d'arrière-plan, cache SQLite de 4 Mo par connexion, compactage et libération après un travail.
- Indexeur : verrou d'écriture pris à la première écriture seulement, cache de 64 Mo et moins de points de contrôle WAL pendant les
  longues écritures (+71 % de lignes/s sur un test isolé), une ligne d'activité par minute au lieu d'une par changement, comptage
  limité à une fois toutes les 2 minutes, événements sans changement réel ignorés.
- Requêtes : le top des fichiers parcourt l'index des tailles sur les grands dossiers. La liste de recherche est plafonnée à 20 000 lignes.

## Reproduire

```powershell
dotnet publish src/Nexus.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o dist
dotnet publish benchmarks/Nexus.Benchmarks -c Release -o <dossier>
<dossier>\Nexus.Benchmarks.exe scan <copie.db> C:\     # premier scan puis rescan
<dossier>\Nexus.Benchmarks.exe stats <copie.db>        # tailles de dossiers et requêtes de stockage
<dossier>\Nexus.Benchmarks.exe apps <copie.db>         # applications
<dossier>\Nexus.Benchmarks.exe dups <copie.db>         # doublons
<dossier>\Nexus.Benchmarks.exe                         # recherche sur 1 M de lignes synthétiques
benchmarks\measure-app.ps1 -Exe dist\Nexus.App.exe -Index <copie.db>   # démarrage, RAM et CPU au repos
```


## Widget de recherche (1.0.3)

Mesures par `benchmarks/test-widget.ps1`, avec une copie de l'index réel (environ 1,1 million de fichiers), sur 3 écrans dont un à 125 %.

| Mesure | Résultat |
|---|---|
| Alt+Espace jusqu'à la fenêtre visible, premier affichage | 25 à 33 ms |
| Réouverture | 14 à 15 ms |
| Du texte aux lignes, debounce de 30 ms et UI Automation compris | 93 à 107 ms |
| Mémoire de travail, widget masqué, 6 s après usage | 8 à 13 Mo (pages rendues au système, elles reviennent à l'usage) |
| Démarrage avec Windows, sans fenêtre, après usage du widget | 46 Mo |
| Démarrage avec Windows, pendant la première analyse | 156 Mo |
| Processeur, widget masqué, sur 10 s | 0,01 % |

Applications dans le widget (1.0.4) : 192 entrées lues dans `shell:AppsFolder` en environ 0,4 s sur un fil d'arrière-plan, puis recherche en mémoire. Lancement d'une application du Store : 274 à 317 ms entre Entrée et l'apparition du processus.
