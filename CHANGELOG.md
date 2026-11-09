# Historique des versions

Le développement a eu lieu hors de Git jusqu'à la 1.0.5 : ce dépôt contient le code de la 1.0.5, sans l'historique détaillé des versions précédentes.

## 1.0.7
- Au démarrage de Windows, Alt+Espace n'attend plus la vérification de l'index : elle se fait ensuite en arrière-plan, et une réparation éventuelle a lieu au lancement suivant.

## 1.0.6
- Le widget propose aussi les programmes installés sans raccourci dans le menu Démarrer, comme VirtualBox (dossiers `Program Files` et `AppData\Local\Programs`).

## 1.0.5
- Quatre tailles pour la barre de recherche (Settings > Search Widget).
- Barre de recherche au thème Onyx de l'application.

## 1.0.4
- Les applications du menu Démarrer (programmes, Store, jeux Steam et Epic) apparaissent en tête du widget et se lancent avec Entrée.

## 1.0.3
- Widget de recherche flottant : Alt+Espace global configurable, navigation au clavier, positions et multi-écrans.
- Icône dans la zone de notification et démarrage avec Windows.
- Recherche approximative fiable sur un gros index.

## 1.0.2
- Affichage de la barre de recherche corrigé.
- Les fichiers fantômes indexés sous un nom court `~1` sont retirés ; un index touché est réparé au premier lancement.
- « Recalculer » vérifie désormais le disque.

## 1.0.1
- Cocher `C:` n'indexe plus `System32`.

## 1.0.0
- Première version : recherche, indexation, stockage, applications, doublons, installateur sans droits administrateur.
