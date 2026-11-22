# DJI RC-N3 FPV per Windows

Usa il tuo radiocomando DJI RC‑N3 via USB nei simulatori FPV su Windows. L’app legge gli stick e i pulsanti e li presenta al simulatore come **controller Xbox 360** oppure come **joystick RC generico**.

## Funzionalità

- **Due uscite virtuali:** Xbox 360 tramite XInput e ViGEmBus, oppure joystick HID/DirectInput tramite vJoy.
- **Calibrazione dei quattro assi:** acquisizione di centro ed estremi, riassegnazione, inversione, zona morta ed expo.
- **Mappatura dei pulsanti:** in modalità Xbox, associa i pulsanti rilevati del RC‑N3 ai comandi del pad. In modalità RC generico diventano i pulsanti joystick 1–4.
- **Profili salvabili:** ogni profilo conserva uscita, calibrazione, mappatura e frequenza USB. Puoi esportare i profili in XML o incorporarli in una copia dell’eseguibile.
- **Selettore fisico C/N/S:** la versione 1.4 permette di acquisire le posizioni Cine, Normal e Sport e associarle a tre profili. I profili devono usare la stessa uscita virtuale per mantenere il controller collegato durante il cambio.
- **Diagnostica USB:** frequenza configurabile da 25 a 250 richieste/s, con misure di frame ricevuti, intervallo P95 e timeout. Le frequenze sopra 100 richieste/s sono sperimentali.
- **Uso in background:** icona nell’area di notifica e avvio con Windows opzionale.

## Avvio rapido

1. Apri `DJI-RC-N3-FPV-Portable.exe` su Windows x64 da una cartella scrivibile.
2. Accendi il RC‑N3 e collegalo con un cavo USB dati.
3. Scegli **Uscita → Xbox 360** oppure **RC generico (vJoy)** e premi **Applica e salva**.
4. Esegui **Calibra…** e controlla gli assi con **Test Windows**.
5. Nel simulatore, associa i quattro assi a rollio, beccheggio, imbardata e gas.

Il PC deve rilevare la porta USB del DJI. L’uscita Xbox richiede ViGEmBus. Per l’uscita RC generico, installa vJoy x64 e configura un dispositivo con gli assi **X, Y, Rx, Ry** e almeno **quattro pulsanti**. **Rz** è facoltativo per la rotella.

## Stato delle verifiche

L’uscita Xbox 360 è stata verificata su Windows con un RC‑N3 reale. Restano da completare il collaudo delle tre posizioni fisiche C/N/S e la verifica dell’uscita vJoy nei singoli simulatori.

Il benchmark mostra le risposte ricevute via USB. La latenza percepita nel simulatore e la frequenza di campionamento fisica degli stick richiedono misure dedicate.

## Build e test

Da PowerShell su Windows x64 con .NET Framework 4.8:

```powershell
.\build.ps1 -Test
.\build.ps1 -Test -ClientDll "C:\percorso\ViGEmClient.dll"
```

Il primo comando compila ed esegue i test. Il secondo include anche il client nativo per l’uscita Xbox. GitHub Actions esegue la build di verifica e i test su Windows a ogni push e pull request.

## Crediti

Il progetto è indipendente da DJI. Usa ViGEmClient/ViGEmBus per l’uscita Xbox e vJoy per l’uscita joystick. Riferimenti e avvisi sui componenti di terze parti sono in `THIRD-PARTY-NOTICES.txt`.
