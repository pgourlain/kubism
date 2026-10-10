Kubism - first launch on macOS
==============================

1. Drag Kubism.app onto the Applications folder.

2. Kubism is not notarized by Apple, so macOS blocks it the first time:
   - Double-click Kubism in Applications. A warning appears: click "Done".
   - Open System Settings > Privacy & Security, scroll down to Security,
     and click "Open Anyway" next to "Kubism was blocked".
   - Confirm with your password, then click "Open".
   You only do this once.

   Or, in Terminal:
     xattr -dr com.apple.quarantine /Applications/Kubism.app

---

Kubism - premier lancement sur macOS
====================================

1. Glissez Kubism.app sur le dossier Applications.

2. Kubism n'est pas notarisé par Apple : macOS le bloque au premier lancement.
   - Double-cliquez sur Kubism dans Applications. Un avertissement s'affiche : cliquez sur "Terminé".
   - Ouvrez Réglages Système > Confidentialité et sécurité, descendez jusqu'à Sécurité,
     puis cliquez sur "Ouvrir quand même" à côté de "Kubism a été bloqué".
   - Confirmez avec votre mot de passe, puis cliquez sur "Ouvrir".
   Cette étape n'est nécessaire qu'une fois.

   Ou, dans le Terminal :
     xattr -dr com.apple.quarantine /Applications/Kubism.app
