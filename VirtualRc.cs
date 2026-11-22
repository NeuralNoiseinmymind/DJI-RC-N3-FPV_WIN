using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcN3 {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {
                using(var mutex=new Mutex(false,@"Local\RCN3-FPV-Portable")) {
                    bool owned;
                    try { owned=mutex.WaitOne(0); } catch(AbandonedMutexException) { owned=true; }
                    if(!owned) {
                        if(!args.Contains("--background"))
                            MessageBox.Show("Il bridge è già aperto. Usa l'icona DJI RC-N3 nell'area di notifica.",
                                "DJI RC-N3 FPV",MessageBoxButtons.OK,MessageBoxIcon.Information);
                        return;
                    }
                    try { Application.Run(new Dashboard(args)); }
                    finally { mutex.ReleaseMutex(); }
                }
            } catch(Exception ex) { MessageBox.Show(ex.Message,"DJI RC-N3 FPV",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        }
    }

    sealed class Dashboard : Form {
        static readonly string[] Sources={"Stick sinistro X","Stick sinistro Y","Stick destro X","Stick destro Y"};
        static readonly string[] Outputs={"Xbox LX","Xbox LY","Xbox RX","Xbox RY"};
        readonly Settings settings;
        Profile profile;
        readonly string forcedPort;
        readonly Bridge bridge;
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=40};
        readonly NotifyIcon tray=new NotifyIcon();
        readonly CheckBox enabled=new CheckBox {Text="Supporto attivo",AutoSize=true};
        readonly CheckBox startup=new CheckBox {Text="Avvia con Windows",AutoSize=true};
        readonly CheckBox buttons=new CheckBox {Text="Tasti DJI",AutoSize=true};
        readonly CheckBox wheel=new CheckBox {Text="Rotella → LT/RT",AutoSize=true};
        readonly ComboBox profiles=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=195};
        readonly ComboBox modes=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=170};
        readonly Label[] axisNames=new Label[4];
        readonly Button mapButtons;
        readonly ComboBox rates=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=65};
        readonly Label status=new Label {AutoSize=true,Font=new Font("Segoe UI",11,FontStyle.Bold)};
        readonly Label live=new Label {AutoSize=true};
        readonly Label hint=new Label {AutoSize=true};
        readonly Label[] buttonIndicators=new Label[4];
        readonly Label[] raw=new Label[4], output=new Label[4];
        readonly ComboBox[] input=new ComboBox[4];
        readonly CheckBox[] invert=new CheckBox[4];
        readonly NumericUpDown[,] fields=new NumericUpDown[4,5];
        readonly Button calibration=new Button {Text="Calibra…",AutoSize=true};
        readonly Button cancel=new Button {Text="Annulla calibrazione",AutoSize=true,Visible=false};
        readonly int[] centers=new int[4], low=new int[4], high=new int[4];
        int calibrationStep, timingRun;
        bool loading, exiting, benchmarking;
        string lastTray="";
        string fileError;

        public Dashboard(string[] args) {
            bool background=args.Contains("--background");
            string requested=ReadArgument(args,"--profile");
            forcedPort=ReadArgument(args,"--port");
            if(forcedPort!=null && !Regex.IsMatch(forcedPort,@"^COM[1-9][0-9]{0,3}$",RegexOptions.IgnoreCase))
                throw new ArgumentException("Usa --port seguito da una porta COM valida.");
            if(args.Contains("--help") || args.Contains("-h")) {
                MessageBox.Show("--background  Avvia nell'area di notifica\n--disabled  Avvia con supporto disattivato\n"+
                    "--profile \"Nome\"  Seleziona un profilo\n--port COM3  Forza la porta per questa sessione",
                    "Parametri DJI RC-N3 FPV");
                Environment.Exit(0);
            }
            try { settings=Files.Read(); }
            catch(Exception ex) { settings=Settings.Defaults(); fileError="Profilo non caricato: "+ex.Message; }
            profile=settings.Profiles.FirstOrDefault(p=>String.Equals(p.Name,requested ?? settings.Selected,
                StringComparison.OrdinalIgnoreCase)) ?? settings.Profiles[0];
            if(requested!=null && !String.Equals(requested,profile.Name,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Profilo inesistente: "+requested);
            Text="DJI RC-N3 FPV · 1.4";
            mapButtons=Button("Mappa tasti…",(s,e)=>EditButtons());
            Font=new Font("Segoe UI",9);
            AutoScaleMode=AutoScaleMode.Dpi;
            StartPosition=FormStartPosition.CenterScreen;
            MinimumSize=new Size(900,470);
            ClientSize=new Size(955,465);
            Icon=SystemIcons.Application;
            BuildUi();
            SelectProfile(profile);
            enabled.Checked=settings.Enabled && !args.Contains("--disabled");
            try { startup.Checked=Setup.Startup; } catch(UnauthorizedAccessException) { startup.Enabled=false; }
            bridge=new Bridge(CurrentOptions());
            enabled.CheckedChanged+=(s,e)=>{
                settings.Enabled=enabled.Checked;
                if(!enabled.Checked) CancelCalibration();
                Publish();
                Store();
            };
            startup.CheckedChanged+=(s,e)=>{
                if(loading) return;
                try { Setup.Startup=startup.Checked; }
                catch(Exception ex) {
                    Alert(ex.Message);
                    loading=true;
                    try { startup.Checked=Setup.Startup; } finally { loading=false; }
                }
            };
            profiles.SelectedIndexChanged+=(s,e)=>{
                if(loading || profiles.SelectedIndex<0) return;
                CancelCalibration();
                SelectProfile(settings.Profiles[profiles.SelectedIndex]);
                Publish();
                Store();
            };
            calibration.Click+=(s,e)=>Calibrate();
            cancel.Click+=(s,e)=>CancelCalibration();
            tray.Icon=SystemIcons.Application;
            tray.Text="DJI RC-N3 FPV";
            tray.ContextMenuStrip=new ContextMenuStrip();
            tray.ContextMenuStrip.Items.Add("Apri pannello",null,(s,e)=>Reveal());
            tray.ContextMenuStrip.Items.Add("Attiva / disattiva supporto",null,(s,e)=>enabled.Checked=!enabled.Checked);
            tray.ContextMenuStrip.Items.Add("Esci",null,(s,e)=>Quit());
            tray.DoubleClick+=(s,e)=>Reveal();
            tray.Visible=true;
            FormClosing+=(s,e)=>{
                if(!exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); }
            };
            FormClosed+=(s,e)=>{
                timer.Stop();
                bridge.Dispose();
                tray.Dispose();
            };
            timer.Tick+=(s,e)=>RefreshTelemetry();
            timer.Start();
            if(!background) Shown+=async (s,e)=> { if(!Setup.HasBus()) await Drivers(); };
            if(background) {
                WindowState=FormWindowState.Minimized;
                ShowInTaskbar=false;
                Shown+=(s,e)=>BeginInvoke((MethodInvoker)delegate { Hide(); });
            }
        }

        static string ReadArgument(string[] args,string name) {
            int index=Array.IndexOf(args,name);
            if(index<0) return null;
            if(index+1>=args.Length || args[index+1].StartsWith("--",StringComparison.Ordinal))
                throw new ArgumentException("Manca il valore per "+name);
            return name=="--port" ? args[index+1].ToUpperInvariant() : args[index+1];
        }

        void BuildUi() {
            var page=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,
                WrapContents=false,AutoScroll=true,Padding=new Padding(15)};
            Controls.Add(page);
            status.Text="Ricerca del radiocomando…";
            page.Controls.Add(status);
            page.Controls.Add(live);
            var toolbar=Row();
            toolbar.Controls.Add(enabled);
            toolbar.Controls.Add(startup);
            toolbar.Controls.Add(Button("Driver…",async (s,e)=>await Drivers()));
            toolbar.Controls.Add(Button("Test Windows",(s,e)=> {
                try { Process.Start("control.exe","joy.cpl"); } catch(Exception ex) { Alert(ex.Message); }
            }));
            toolbar.Controls.Add(Button("Misura USB…",(s,e)=>Benchmark()));
            toolbar.Controls.Add(Button("Info",(s,e)=>ShowNotices()));
            page.Controls.Add(toolbar);
            var modeRow=Row();
            modeRow.Controls.Add(new Label {Text="Uscita",AutoSize=true,Margin=new Padding(3,8,5,0)});
            modes.Items.AddRange(new object[]{"Xbox 360","RC generico (vJoy)"});
            modes.SelectedIndexChanged+=(s,e)=>UpdateModeLabels();
            modeRow.Controls.Add(modes);
            modeRow.Controls.Add(Button("Selettore C/N/S…",(s,e)=>ConfigureSwitch()));
            modeRow.Controls.Add(Button("Driver RC…",(s,e)=>OpenRcDriver()));
            page.Controls.Add(modeRow);
            var options=Row();
            options.Controls.Add(new Label {Text="Profilo",AutoSize=true,Margin=new Padding(3,8,5,0)});
            options.Controls.Add(profiles);
            options.Controls.Add(Button("Gestisci…",(s,e)=>ManageProfiles()));
            options.Controls.Add(mapButtons);
            options.Controls.Add(new Label {Text="Richieste/s",AutoSize=true,Margin=new Padding(10,8,3,0)});
            rates.Items.AddRange(UsbRates.Supported.Cast<object>().ToArray());
            options.Controls.Add(rates);
            options.Controls.Add(buttons);
            options.Controls.Add(wheel);
            page.Controls.Add(options);
            var table=new TableLayoutPanel {AutoSize=true,ColumnCount=10,RowCount=5,Padding=new Padding(0,7,0,7)};
            string[] titles={"Asse","Sorgente","Grezzo","Uscita","Min","Centro","Max","Dead %","Expo %","Inv."};
            for(int j=0;j<titles.Length;j++) table.Controls.Add(new Label {Text=titles[j],AutoSize=true},j,0);
            for(int i=0;i<4;i++) {
                axisNames[i]=new Label {Text=Outputs[i],AutoSize=true,Margin=new Padding(3,7,12,0)};
                table.Controls.Add(axisNames[i],0,i+1);
                input[i]=new ComboBox {Width=140,DropDownStyle=ComboBoxStyle.DropDownList};
                input[i].Items.AddRange(Sources);
                table.Controls.Add(input[i],1,i+1);
                raw[i]=new Label {Text="—",Width=55,Margin=new Padding(3,7,3,0)};
                output[i]=new Label {Text="—",Width=58,Margin=new Padding(3,7,3,0)};
                table.Controls.Add(raw[i],2,i+1);
                table.Controls.Add(output[i],3,i+1);
                for(int j=0;j<5;j++) {
                    fields[i,j]=new NumericUpDown {Width=66,Maximum=j<3?65535:j==3?30:100};
                    table.Controls.Add(fields[i,j],4+j,i+1);
                }
                invert[i]=new CheckBox {AutoSize=true};
                table.Controls.Add(invert[i],9,i+1);
            }
            page.Controls.Add(table);
            var actions=Row();
            actions.Controls.Add(Button("Applica e salva",(s,e)=>SaveProfile()));
            actions.Controls.Add(calibration);
            actions.Controls.Add(cancel);
            actions.Controls.Add(Button("Crea EXE con profili…",(s,e)=>Export()));
            actions.Controls.Add(Button("Nascondi",(s,e)=>Hide()));
            actions.Controls.Add(Button("Esci",(s,e)=>Quit()));
            page.Controls.Add(actions);
            hint.Text=fileError ?? "Leggi i valori in tempo reale. Salva le modifiche prima di esportare.";
            page.Controls.Add(hint);
        }

        static FlowLayoutPanel Row() {
            return new FlowLayoutPanel {AutoSize=true,WrapContents=false,Margin=new Padding(0,6,0,4)};
        }

        static Button Button(string text,EventHandler click) {
            var item=new Button {Text=text,AutoSize=true};
            item.Click+=click;
            return item;
        }

        void SelectProfile(Profile selected) {
            loading=true;
            try {
                profile=selected;
                settings.Profiles.Sort((a,b)=>StringComparer.CurrentCultureIgnoreCase.Compare(a.Name,b.Name));
                settings.Selected=profile.Name;
                profiles.Items.Clear();
                profiles.Items.AddRange(settings.Profiles.Select(p=>(object)p.Name).ToArray());
                profiles.SelectedIndex=settings.Profiles.IndexOf(profile);
                rates.SelectedItem=profile.Rate;
                modes.SelectedIndex=(int)profile.Output;
                buttons.Checked=profile.Buttons;
                wheel.Checked=profile.Wheel;
                for(int i=0;i<4;i++) {
                    Axis axis=profile.Axes[i];
                    input[i].SelectedIndex=axis.Input;
                    invert[i].Checked=axis.Invert;
                    int[] values={axis.Min,axis.Center,axis.Max,axis.Dead,axis.Expo};
                    for(int j=0;j<5;j++) fields[i,j].Value=values[j];
                }
            } finally { loading=false; }
        }

        Profile FromEditor(string name) {
            if(input.Select(box=>box.SelectedIndex).Distinct().Count()!=4)
                throw new InvalidOperationException("Assegna ciascuno stick una sola volta.");
            var edited=new Profile {Name=name,Rate=(int)rates.SelectedItem,Buttons=buttons.Checked,
                Wheel=wheel.Checked, Bindings=profile.Bindings, Output=(OutputMode)modes.SelectedIndex};
            for(int i=0;i<4;i++) {
                var a=new Axis {Input=input[i].SelectedIndex,Invert=invert[i].Checked,Min=(int)fields[i,0].Value,
                    Center=(int)fields[i,1].Value,Max=(int)fields[i,2].Value,Dead=(int)fields[i,3].Value,
                    Expo=(int)fields[i,4].Value};
                if(a.Min>=a.Center || a.Center>=a.Max)
                    throw new InvalidOperationException(Outputs[i]+": servono minimo < centro < massimo.");
                edited.Axes[i]=a;
            }
            return edited;
        }

        bool SaveProfile() {
            if(calibrationStep!=0) { Alert("Completa o annulla la calibrazione."); return false; }
            try {
                Profile edited=FromEditor(profile.Name);
                settings.Profiles[settings.Profiles.IndexOf(profile)]=edited;
                SelectProfile(edited);
                Publish();
                return Store();
            } catch(Exception ex) { Alert(ex.Message); return false; }
        }

        void EditButtons() {
            if(calibrationStep!=0) return;
            XboxControl[] choices=(XboxControl[])Enum.GetValues(typeof(XboxControl));
            string[] labels={"Nessuna azione","A","B","X","Y","LB","RB","LT","RT","Back","Start",
                "Stick sinistro (LS)","Stick destro (RS)","D-pad su","D-pad giù","D-pad sinistra","D-pad destra"};
            using(var dialog=new Form {Text="Mappa tasti · "+profile.Name,ClientSize=new Size(445,219),
                StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false,MinimizeBox=false}) {
                var grid=new TableLayoutPanel {AutoSize=true,Location=new Point(10,10),ColumnCount=3};
                var targets=new ComboBox[4];
                XboxControl[] current=profile.Bindings.Values;
                for(int i=0;i<4;i++) {
                    grid.Controls.Add(new Label {Text=ButtonBindings.Sources[i],Width=125,Margin=new Padding(3,7,3,0)},0,i);
                    targets[i]=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=170};
                    targets[i].Items.AddRange(labels);
                    targets[i].SelectedIndex=Array.IndexOf(choices,current[i]);
                    grid.Controls.Add(targets[i],1,i);
                    buttonIndicators[i]=new Label {Width=70,Text="—",Margin=new Padding(3,7,3,0)};
                    grid.Controls.Add(buttonIndicators[i],2,i);
                }
                var open=Button("Ripristina A/B/X/Y",(sender,args)=>{
                    for(int i=0;i<4;i++) targets[i].SelectedIndex=i+1;
                });
                open.Location=new Point(12,176);
                var save=Button("Salva mappa",(sender,args)=>{
                    try {
                        var edited=FromEditor(profile.Name);
                        edited.Bindings=new ButtonBindings {
                            Photo=choices[targets[0].SelectedIndex], Rth=choices[targets[1].SelectedIndex],
                            Camera=choices[targets[2].SelectedIndex], Function=choices[targets[3].SelectedIndex]
                        };
                        settings.Profiles[settings.Profiles.IndexOf(profile)]=edited;
                        SelectProfile(edited); Publish();
                        if(Store()) dialog.Close();
                    } catch(Exception ex) { Alert(ex.Message); }
                });
                save.Location=new Point(330,176);
                dialog.Controls.Add(grid); dialog.Controls.Add(open); dialog.Controls.Add(save);
                dialog.AcceptButton=save;
                try { dialog.ShowDialog(this); }
                finally { Array.Clear(buttonIndicators,0,buttonIndicators.Length); }
            }
        }

        string AskName(string title, string suggested, Profile excluded) {
            using(var dialog=new Form {Text=title,ClientSize=new Size(350,95),
                StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false,MinimizeBox=false}) {
                var name=new TextBox {Text=suggested,Left=12,Top=12,Width=322,MaxLength=70};
                var ok=new Button {Text="Salva",DialogResult=DialogResult.OK,Left=256,Top=50};
                dialog.Controls.Add(name); dialog.Controls.Add(ok); dialog.AcceptButton=ok;
                if(dialog.ShowDialog(this)!=DialogResult.OK) return null;
                string result=name.Text.Trim();
                if(result.Length==0 || settings.Profiles.Any(p=>p!=excluded &&
                   String.Equals(p.Name,result,StringComparison.OrdinalIgnoreCase))) {
                    Alert("Scegli un nome univoco per il profilo.");
                    return null;
                }
                return result;
            }
        }

        void ManageProfiles() {
            if(calibrationStep!=0) return;
            using(var dialog=new Form {Text="Gestisci profili",ClientSize=new Size(560,290),
                StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false,MinimizeBox=false}) {
                var list=new ListBox {Location=new Point(12,12),Size=new Size(536,180)};
                Action<Profile> refresh=selected=>{
                    list.Items.Clear();
                    list.Items.AddRange(settings.Profiles.Cast<object>().ToArray());
                    list.SelectedItem=selected;
                };
                Action<Profile> commit=selected=>{
                    SelectProfile(selected); Publish(); Store(); refresh(selected);
                };
                var line=Row();
                line.Location=new Point(9,198);
                line.Controls.Add(Button("Attiva",(s,e)=>{
                    var selected=list.SelectedItem as Profile;
                    if(selected!=null) commit(selected);
                }));
                line.Controls.Add(Button("Duplica…",(s,e)=>{
                    var selected=list.SelectedItem as Profile;
                    if(selected==null || settings.Profiles.Count>=100) return;
                    string name=AskName("Duplica profilo",selected.Name.Substring(0,Math.Min(63,selected.Name.Length))+" copia",null);
                    if(name==null) return;
                    var copy=new Profile {Name=name,Rate=selected.Rate,Buttons=selected.Buttons,Wheel=selected.Wheel,
                        Axes=selected.Axes,Bindings=selected.Bindings,Output=selected.Output};
                    settings.Profiles.Add(copy);
                    commit(copy);
                }));
                line.Controls.Add(Button("Rinomina…",(s,e)=>{
                    var selected=list.SelectedItem as Profile;
                    if(selected==null) return;
                    string name=AskName("Rinomina profilo",selected.Name,selected);
                    if(name!=null) {
                        if(settings.ModeSwitch!=null)
                            foreach(var binding in settings.ModeSwitch)
                                if(binding!=null && binding.Profile==selected.Name) binding.Profile=name;
                        selected.Name=name;
                        commit(profile);
                    }
                }));
                line.Controls.Add(Button("Elimina…",(s,e)=>{
                    var selected=list.SelectedItem as Profile;
                    if(selected==null || settings.Profiles.Count<2) return;
                    if(MessageBox.Show(dialog,"Eliminare il profilo "+selected.Name+"?","Elimina profilo",
                        MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
                    if(settings.ModeSwitch!=null)
                        foreach(var binding in settings.ModeSwitch)
                            if(binding!=null && binding.Profile==selected.Name) binding.Profile=null;
                    settings.Profiles.Remove(selected);
                    commit(selected==profile?settings.Profiles[0]:profile);
                }));
                var backup=Row();
                backup.Location=new Point(9,238);
                backup.Controls.Add(Button("Esporta profili…",(s,e)=>{
                    using(var save=new SaveFileDialog {Filter="Profili DJI (*.xml)|*.xml",
                      FileName="DJI-RC-N3-backup.xml"}) {
                        if(save.ShowDialog(dialog)==DialogResult.OK) {
                            try { Files.Export(save.FileName,settings); } catch(Exception ex) { Alert(ex.Message); }
                        }
                    }
                }));
                backup.Controls.Add(Button("Importa profili…",(s,e)=>{
                    using(var open=new OpenFileDialog {Filter="Profili DJI (*.xml)|*.xml"}) {
                        if(open.ShowDialog(dialog)!=DialogResult.OK) return;
                        try {
                            var incoming=Files.Import(open.FileName);
                            if(settings.Profiles.Count+incoming.Profiles.Count>100)
                                throw new InvalidDataException("Il limite complessivo è 100 profili.");
                            foreach(var entry in incoming.Profiles) {
                                string name=entry.Name.Substring(0,Math.Min(62,entry.Name.Length));
                                for(int i=2;settings.Profiles.Any(p=>String.Equals(p.Name,entry.Name,
                                     StringComparison.OrdinalIgnoreCase));i++) entry.Name=name+" ("+i+")";
                                settings.Profiles.Add(entry);
                            }
                            commit(profile);
                        } catch(Exception ex) { Alert(ex.Message); }
                    }
                }));
                dialog.Controls.Add(list); dialog.Controls.Add(line); dialog.Controls.Add(backup);
                refresh(profile);
                dialog.ShowDialog(this);
            }
        }

        void Export() {
            if(!SaveProfile()) return;
            using(var choose=new SaveFileDialog {Filter="Applicazione Windows (*.exe)|*.exe",
                FileName="DJI-RC-N3-FPV-Profilato.exe",AddExtension=true,DefaultExt="exe"}) {
                if(choose.ShowDialog(this)!=DialogResult.OK) return;
                try {
                    Files.CopyWithProfiles(choose.FileName,settings);
                    hint.Text="Eseguibile creato con i profili inclusi.";
                } catch(Exception ex) { Alert(ex.Message); }
            }
        }

        bool Store() {
            try {
                Files.Save(settings);
                hint.Text="Profili salvati accanto all'eseguibile.";
                return true;
            } catch(Exception ex) {
                hint.Text="Impostazioni attive solo per questa sessione.";
                Alert("Salvataggio profili non riuscito. Apri l'app da una cartella scrivibile.\n"+ex.Message);
                return false;
            }
        }

        Options CurrentOptions() {
            Dictionary<int,Profile> mapped=null;
            if(!benchmarking && calibrationStep==0 && settings.ModeSwitch!=null) {
                mapped=new Dictionary<int,Profile>();
                foreach(var binding in settings.ModeSwitch) {
                    if(binding==null || binding.Code<0 || binding.Code>3) continue;
                    var target=settings.Profiles.FirstOrDefault(p=>p.Name==binding.Profile);
                    if(target!=null && target.Output==profile.Output) mapped[binding.Code]=target;
                }
            }
            return new Options {Enabled=enabled.Checked,Calibrating=calibrationStep!=0,
                ForcedPort=forcedPort,Profile=profile,SwitchProfiles=mapped};
        }

        void ConfigureSwitch() {
            if(calibrationStep!=0 || benchmarking) return;
            using(var dialog=new Form {Text="Selettore fisico C/N/S",ClientSize=new Size(565,260),
                StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false,MinimizeBox=false}) {
                var note=new Label {Text="Sposta la leva su C, N e S e acquisisci ogni posizione. Assegna un profilo con la stessa uscita del profilo manuale.",
                    Location=new Point(14,12),Size=new Size(532,40)};
                var grid=new TableLayoutPanel {AutoSize=true,Location=new Point(14,58),ColumnCount=4};
                var codes=new int[3];
                var names=new ComboBox[3];
                var readings=new Label[3];
                var current=settings.ModeSwitch;
                var candidates=settings.Profiles.Where(p=>p.Output==profile.Output).Select(p=>p.Name).ToArray();
                for(int i=0;i<3;i++) {
                    int row=i;
                    var binding=current!=null && current.Length==3 ? current[i] : null;
                    codes[i]=binding!=null?binding.Code:-1;
                    grid.Controls.Add(new Label {Text=new[]{"Cine (C)","Normal (N)","Sport (S)"}[i],AutoSize=true,
                        Margin=new Padding(3,8,12,0)},0,i);
                    readings[i]=new Label {Text=codes[i]<0?"—":codes[i].ToString(),Width=30,
                        Margin=new Padding(3,8,5,0)};
                    grid.Controls.Add(readings[i],1,i);
                    grid.Controls.Add(Button("Acquisisci",(s,e)=>{
                        var sample=bridge.Latest;
                        if(sample==null || !sample.SwitchCode.HasValue) {
                            MessageBox.Show(dialog,"Dati del selettore assenti. Collega il RC-N3 acceso."); return;
                        }
                        codes[row]=sample.SwitchCode.Value;
                        readings[row].Text=codes[row].ToString();
                    }),2,i);
                    names[i]=new ComboBox {Width=230,DropDownStyle=ComboBoxStyle.DropDownList};
                    names[i].Items.Add("Usa profilo manuale");
                    names[i].Items.AddRange(candidates);
                    int selected=binding==null ? -1 : Array.IndexOf(candidates,binding.Profile);
                    names[i].SelectedIndex=selected+1;
                    grid.Controls.Add(names[i],3,i);
                }
                var actions=Row();
                actions.Location=new Point(14,164);
                actions.Controls.Add(Button("Salva",(s,e)=>{
                    if(codes.Any(code=>code<0 || code>3) || codes.Distinct().Count()!=3) {
                        MessageBox.Show(dialog,"Acquisisci tre posizioni fisiche con tre codici distinti.");
                        return;
                    }
                    settings.ModeSwitch=Enumerable.Range(0,3).Select(i=>new SwitchBinding {
                        Code=codes[i],Profile=names[i].SelectedIndex>0 ? (string)names[i].SelectedItem : null
                    }).ToArray();
                    Publish();
                    if(Store()) dialog.Close();
                }));
                actions.Controls.Add(Button("Disattiva selettore",(s,e)=>{
                    settings.ModeSwitch=null;
                    Publish();
                    if(Store()) dialog.Close();
                }));
                dialog.Controls.Add(note); dialog.Controls.Add(grid); dialog.Controls.Add(actions);
                dialog.ShowDialog(this);
            }
        }

        void Publish() { bridge.Options=CurrentOptions(); }

        void Calibrate() {
            Sample sample=bridge.Latest;
            if(sample==null || sample.Raw==null) { Alert("Collega il DJI RC-N3 e attiva il supporto."); return; }
            if(calibrationStep==0) {
                calibrationStep=1;
                Publish();
                calibration.Text="Registra il centro";
                cancel.Visible=true;
                hint.Text="1/2  Lascia tutti gli stick al centro, poi premi Registra il centro.";
            } else if(calibrationStep==1) {
                Array.Copy(sample.Raw,centers,4);
                Array.Copy(centers,low,4); Array.Copy(centers,high,4);
                int[] discarded;
                while(bridge.Calibration.TryDequeue(out discarded)) { }
                calibrationStep=2;
                calibration.Text="Registra gli estremi";
                hint.Text="2/2  Muovi entrambi gli stick in tutti e quattro gli angoli, poi registra gli estremi.";
            } else {
                Collect();
                if(Enumerable.Range(0,4).Any(i=>centers[i]-low[i]<80 || high[i]-centers[i]<80)) {
                    Alert("Servono entrambi gli estremi per tutti e quattro gli assi."); return;
                }
                for(int i=0;i<4;i++) {
                    int source=input[i].SelectedIndex;
                    fields[i,0].Value=low[source];
                    fields[i,1].Value=centers[source];
                    fields[i,2].Value=high[source];
                }
                CancelCalibration();
                SaveProfile();
            }
        }

        void Collect() {
            int[] sample;
            while(bridge.Calibration.TryDequeue(out sample)) {
                if(calibrationStep!=2) continue;
                for(int i=0;i<4;i++) { low[i]=Math.Min(low[i],sample[i]); high[i]=Math.Max(high[i],sample[i]); }
            }
        }

        void CancelCalibration() {
            if(calibrationStep==0) return;
            calibrationStep=0;
            Publish();
            calibration.Text="Calibra…";
            cancel.Visible=false;
            hint.Text="Calibrazione terminata.";
        }

        void RefreshTelemetry() {
            Collect();
            Sample latest=bridge.Latest;
            if(latest==null) return;
            string switchState="";
            if(latest.SwitchCode.HasValue) {
                int position=settings.ModeSwitch==null ? -1 :
                    Array.FindIndex(settings.ModeSwitch,b=>b!=null && b.Code==latest.SwitchCode.Value);
                switchState=position<0 || position>2 ? " · selettore "+latest.SwitchCode.Value :
                    " · "+("CNS"[position])+" · "+latest.ActiveProfile;
            }
            status.Text=latest.Status+switchState;
            if(lastTray!=latest.Status) {
                lastTray=latest.Status;
                string tip="DJI RC-N3 FPV · "+latest.Status;
                tray.Text=tip.Substring(0,Math.Min(63,tip.Length));
            }
            live.Text=latest.Raw==null ? "—" : latest.Mode==OutputMode.GenericRc
                ? String.Format("{0:0.0} frame/s · P95 {1:0.0} ms · Timeout {2} · Tasti RC 0x{3:X1}{4}",
                    latest.Hz,latest.P95,latest.Stalls,calibrationStep==0 && !benchmarking ? latest.PhysicalButtons : 0,
                    calibrationStep==0 && !benchmarking ? "" : " · uscita neutralizzata")
                : String.Format(
                "{0:0.0} frame/s · P95 intervallo {1:0.0} ms · Timeout {2} · Xbox 0x{3:X4} · LT {4} · RT {5}{6}",
                latest.Hz,latest.P95,latest.Stalls,latest.Buttons,latest.LeftTrigger,latest.RightTrigger,
                calibrationStep==0 && !benchmarking?"":" · uscita neutralizzata");
            for(int i=0;i<buttonIndicators.Length;i++)
                if(buttonIndicators[i]!=null)
                    buttonIndicators[i].Text=(latest.PhysicalButtons&(1<<i))!=0?"Premuto":"—";
            for(int i=0;i<4;i++) {
                int source=input[i].SelectedIndex;
                raw[i].Text=latest.Raw==null?"—":latest.Raw[source].ToString();
                output[i].Text=latest.Output==null?"—":latest.Output[i].ToString();
            }
        }

        void Benchmark() {
            if(benchmarking || calibrationStep!=0) return;
            if(!enabled.Checked || bridge.Latest==null || bridge.Latest.Raw==null) {
                Alert("Collega il radiocomando prima di misurare la frequenza USB.");
                return;
            }
            benchmarking=true;
            try {
                using(var report=new Form {Text="Misura USB DJI",ClientSize=new Size(515,245),
                    StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                    MaximizeBox=false,MinimizeBox=false}) {
                    var output=new TextBox {ReadOnly=true,Multiline=true,Dock=DockStyle.Fill,
                        Font=new Font("Consolas",10),ScrollBars=ScrollBars.Vertical};
                    report.Controls.Add(output);
                    report.Shown+=async (sender,args)=>{
                        try {
                            output.AppendText("Richieste/s  Frame/s  P95 intervallo  Timeout (>45 ms)\r\n");
                            foreach(int rate in UsbRates.Supported.Where(r=>r>=100)) {
                                if(report.IsDisposed || exiting) break;
                                var candidate=new Profile {Name=profile.Name, Rate=rate, Axes=profile.Axes,
                                    Buttons=profile.Buttons, Wheel=profile.Wheel, Bindings=profile.Bindings, Output=profile.Output};
                                bridge.Options=new Options {Enabled=true, ForcedPort=forcedPort,
                                    Profile=candidate, NeutralOutput=true, TimingRun=++timingRun};
                                await Task.Delay(2700);
                                if(report.IsDisposed || exiting) break;
                                Sample sample=bridge.Latest;
                                if(sample==null || sample.Raw==null || sample.Rate!=rate || sample.TimingRun!=timingRun) {
                                    output.AppendText(String.Format("{0,11}  Nessun campione\r\n",rate));
                                    break;
                                }
                                output.AppendText(String.Format("{0,11}  {1,7:0.0}  {2,11:0.0} ms  {3,7}\r\n",
                                    rate,sample.Hz,sample.P95,sample.Stalls));
                            }
                            if(!report.IsDisposed && !exiting)
                                output.AppendText("\r\nProfilo precedente ripristinato.\r\nI frame validi non indicano campioni fisici distinti.");
                        } finally { if(!exiting && !IsDisposed) Publish(); }
                    };
                    report.ShowDialog(this);
                }
            } finally {
                benchmarking=false;
                if(!exiting && !IsDisposed) Publish();
            }
        }

        void UpdateModeLabels() {
            bool generic=modes.SelectedIndex==(int)OutputMode.GenericRc;
            mapButtons.Enabled=!generic;
            wheel.Text=generic?"Rotella su Rz":"Rotella su LT/RT";
            string[] labels=generic?new[]{"RC X","RC Y","RC Rx","RC Ry"}:Outputs;
            for(int i=0;i<axisNames.Length;i++)
                if(axisNames[i]!=null) axisNames[i].Text=labels[i];
        }

        void OpenRcDriver() {
            using(var dialog=new Form {Text="Driver per RC generico",StartPosition=FormStartPosition.CenterParent,
                ClientSize=new Size(505,120),FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false,MinimizeBox=false}) {
                dialog.Controls.Add(new Label {
                    Text="Serve vJoy x64. Configura un dispositivo con gli assi X, Y, Rx, Ry e almeno 4 pulsanti. Rz è facoltativo per la rotella.",
                    AutoSize=true,MaximumSize=new Size(460,0),Location=new Point(15,15)});
                var link=Button("Apri i download di vJoy",(s,e)=>{
                    try { Process.Start(VirtualRc.DriverUrl); } catch(Exception ex) { Alert(ex.Message); }
                });
                link.Location=new Point(15,70);
                dialog.Controls.Add(link);
                dialog.ShowDialog(this);
            }
        }

        void ShowNotices() {
            using(var form=new Form {Text="Informazioni e licenze",Size=new Size(740,520),StartPosition=FormStartPosition.CenterParent})
            using(var notices=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices"))
            using(var reader=new StreamReader(notices)) {
                var details=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
                details.Text="DJI RC-N3 FPV Portable · Windows x64"+Environment.NewLine+
                    (Setup.HasDriverBundle ? "Driver inclusi: DJI USB VCOM e ViGEmBus 1.17.333." :
                    "Driver esterni: DJI USB VCOM e ViGEmBus. Usa il pulsante Driver.")+
                    Environment.NewLine+"Impostazioni: "+Files.Config+Environment.NewLine+Environment.NewLine+reader.ReadToEnd();
                form.Controls.Add(details);
                form.ShowDialog(this);
            }
        }

        async Task Drivers() {
            if(!Setup.HasDriverBundle) {
                using(var dialog=new Form {Text="Driver per Windows",StartPosition=FormStartPosition.CenterParent,
                    ClientSize=new Size(465,110)}) {
                    var row=Row();
                    row.Location=new Point(10,15);
                    row.Controls.Add(Button("DJI Assistant 2",(s,e)=>Process.Start(
                        "https://www.dji.com/downloads/softwares/dji-assistant-2-consumer-drones-series")));
                    row.Controls.Add(Button("ViGEmBus",(s,e)=>Process.Start(
                        "https://github.com/nefarius/ViGEmBus/releases/latest")));
                    dialog.Controls.Add(row);
                    dialog.ShowDialog(this);
                }
                return;
            }
            if(MessageBox.Show(this,"La procedura installa solo i componenti mancanti. Collega il DJI acceso. Windows può richiedere l'autorizzazione amministrativa.",
                Text,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK) return;
            try {
                hint.Text="Verifica e installazione driver in corso…";
                string result=await Task.Run(()=>Setup.Install());
                MessageBox.Show(this,result,Text);
                hint.Text="Controllo driver terminato.";
            } catch(Exception ex) { Alert(ex.Message); }
        }

        void Reveal() {
            ShowInTaskbar=true; Show(); WindowState=FormWindowState.Normal; Activate();
        }

        void Quit() { exiting=true; Close(); }
        void Alert(string message) { MessageBox.Show(this,message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }
}

