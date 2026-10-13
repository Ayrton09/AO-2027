; LA DESINSTALACION NO DEJA NADA (el autor, 12/10: "la idea es que la desinstalacion sea completa").
; El instalador de electron-builder se copia a %LOCALAPPDATA%\coverao-escritorio-updater para electron-updater, que
; esta app no usa (se actualiza con actualizacion.js): se borra al terminar de instalar y al desinstalar. Los datos de
; la app (%APPDATA%\coverao-escritorio) los borra deleteAppDataOnUninstall (package.json), salvo en una actualizacion.

!macro customInstall
  ${if} $installMode == "all"
    SetShellVarContext current
  ${endif}
  RMDir /r "$LOCALAPPDATA\coverao-escritorio-updater"
  ${if} $installMode == "all"
    SetShellVarContext all
  ${endif}
!macroend

!macro customUnInstall
  ${ifNot} ${isUpdated}
    ${if} $installMode == "all"
      SetShellVarContext current
    ${endif}
    RMDir /r "$LOCALAPPDATA\coverao-escritorio-updater"
    ${if} $installMode == "all"
      SetShellVarContext all
    ${endif}
  ${endIf}
!macroend
