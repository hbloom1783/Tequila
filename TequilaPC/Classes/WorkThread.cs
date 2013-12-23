﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using System.ComponentModel;
using System.Net;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;

namespace Tequila
{
    class WorkThread
    {
        #region member declarations
            public static bool DontDownloadManifest = false;
            public static bool DontSelfUpdate = false;
            public static bool GenerageChecksumToClipboard = false;
        
            private ArrayList m_ErrorLog = new ArrayList();
            private ArrayList m_WarningLog = new ArrayList();
            private ArrayList m_ManifestFileList;
            private ArrayList m_DownloadQueue = new ArrayList();
            private long m_DownloadSize = 0;
            private long m_Downloaded = 0;
            private long m_CurrDownloadBytes = 0;

            private HTTP client = new HTTP();

            public string LocalManifest = "";
            public string PathRoot = "";
            public string ManifestURL; 

            private XElement Log;
            private XElement LogNew = new XElement("files");

            private string m_Status = "";
            private int m_progress = 0;
            private string m_current = "";
            XElement m_manifest;
        #endregion

        public static bool Kill = false;
        private Thread myWorkThread;

        public XElement Manifest {
            get { return m_manifest; }
        }

        public WorkThread(string ManifestoURL)
        {
            ManifestURL = ManifestoURL;
        }

        public string ErrorMessage
        {
            get
            {
                string Message = "";

                foreach (string s in m_ErrorLog)
                {
                    Message += s;
                }

                return Message;
            }
        }
        public string WarningMessage
        {
            get
            {
                string Message = "";

                foreach (string s in m_WarningLog)
                {
                    Message += s;
                }

                return Message;
            }
        }

        public string Status { 
            get { 
                return m_Status; 
            } set { 
                m_Status = Status; 
            }
        }

        public int CurProgress { get { return m_progress; } }
        public string CurFile { get { return m_current; } }

        public string LogPath {
            get {
                return Path.Combine(Settings.GamePath, "tequilalog.xml");
            }
        }

        public void LoadLog()
        {
            if (System.IO.File.Exists(LogPath))
            {
                Log = XElement.Load(LogPath);
            }
        }

        private void FlagVerified(string file, long size, string md5)
        {
            try
            {
                FileInfo fi = new FileInfo(Path.Combine(Settings.GamePath, file));
                
                XElement e = new XElement("file");
                e.Add(new XAttribute("name", file));
                e.Add(new XAttribute("size", size));
                e.Add(new XAttribute("md5", md5));
                e.Add(new XAttribute("ModDate", fi.LastWriteTime.ToString(fi.LastWriteTime.ToString("yyyy.MM.dd.HH.mm.ss"))));

                LogNew.Add(e);

                // Sometimes we may run this through too fast and the file may be locked    
                // we will retry a save attempt for up to 3 seconds, if we are not able     
                // to save without errors by then we do one final save attempt and          
                // report error.                                                            
                bool saveSuccessful = false;
                Stopwatch sw = new Stopwatch();
                sw.Start();

                while (sw.ElapsedMilliseconds < 3000 && !saveSuccessful) {
                    try
                    {
                        LogNew.Save(LogPath);
                        saveSuccessful = true;
                    } catch (Exception ex) {
                        saveSuccessful = false;
                    }
                }

                // OK, if we did not save, here is our last attempt!                        
                if (!saveSuccessful) LogNew.Save(LogPath);

            } catch (Exception ex) {
                MyToolkit.ErrorReporter(ex, "WorkThread.FlagVerified");
            }
        }

        private bool AlreadyVerified(string file, long size, string md5) 
        {
            try
            {
                if(!File.Exists(Path.Combine(Settings.GamePath, file))) return false;

                XElement match = (from el in Log.Descendants("file")
                                    where (string)el.Attribute("name") == file
                                    select el).First();

                FileInfo fi = new FileInfo(Path.Combine(Settings.GamePath, file));

                string lastModDate;
                if (match.Attribute("ModDate") == null)
                    lastModDate = fi.LastWriteTime.ToString("yyyy.MM.dd.HH.mm.ss");
                else
                    lastModDate = match.Attribute("ModDate").Value;

                if(fi.Length != size) {
                    return false;
                }
                else if (lastModDate != fi.LastWriteTime.ToString(fi.LastWriteTime.ToString("yyyy.MM.dd.HH.mm.ss")))
                {
                    return false;
                } else if (long.Parse(match.Attribute("size").Value) == size 
                    && match.Attribute("md5").Value.ToLower() == md5) {
                    return true;
                } else {
                    return false;
                }
            }
            catch (Exception ex) {
                return false;
            }
        }

        public void Validate()
        {
            long i = 0;
            m_Status = "Validating";

            LoadLog();

            foreach (Fingerprint ManifestFingerprint in m_ManifestFileList)
            {
                if (Kill) return;

                i++;
                m_current = ManifestFingerprint.FullName;

                ProgressEventArgs e = new ProgressEventArgs(i, m_ManifestFileList.Count);

                Fingerprint LocalFingerprint;
                if (System.IO.File.Exists(ManifestFingerprint.FullName))
                {
                    // File exists locally, lets start verifying it. First check if     
                    // the checksum matches the one in our last run log, if so there    
                    // is no need to download the file nor do a checksum                
                    if (AlreadyVerified(ManifestFingerprint.FileName,
                                        ManifestFingerprint.Size,
                                        ManifestFingerprint.Checksum))						
					{
						FlagVerified(ManifestFingerprint.FileName, ManifestFingerprint.Size, ManifestFingerprint.Checksum);
					}
					else
					{
                        // Get an md5 checksum for the local copy of the file       
                        LocalFingerprint = new Fingerprint(ManifestFingerprint.RootPath, ManifestFingerprint.FileName);

						if (LocalFingerprint.Equals(ManifestFingerprint))
						{
							FlagVerified(ManifestFingerprint.FileName, ManifestFingerprint.Size, ManifestFingerprint.Checksum);
						} else {
                            // There was no match, lets add the file to our download queue          
                            AddToDownloadQueue(ManifestFingerprint);
                        }
                    }
                } else { 
                    // File does not exist locally, we must download it. Add to our download queue.
                    AddToDownloadQueue(ManifestFingerprint);
                }
                m_progress = (int)(Math.Round((i / 100.0f) * 100.0f));
            }

            DownloadFiles();
        }

        private void AddToDownloadQueue(Fingerprint file) {
            if (file.DownloadURL != "")
            {
                m_DownloadQueue.Add(file);
                m_DownloadSize += file.Size;
            } else { 
                string Error;
                Error = "The following file has an invalid checksum. You will need to obtain it from a valid game installation:\r\n" + file.FileName + "\r\n";
                m_ErrorLog.Add(Error);
            }
        }

        private bool m_DownloadActive = false;
        public void DownloadFiles() {

            foreach (Fingerprint file in m_DownloadQueue)
            {
                if (Kill) return;

                HTTP client = new HTTP();

                if (client.StartDownload(new AsyncCompletedEventHandler(DownloadFileComplete),
                                     new DownloadProgressChangedEventHandler(dlProgress),
                                     file.DownloadURL,
                                     file.FullName + ".download")){
                    m_Status = "Downloading";
                    m_DownloadActive = true;
                }

                m_current = file.FullName;
                
                while (m_DownloadActive) {
                    if (Kill)
                    {
                        client.CancelDownload();
                        return;
                    }
                    System.Threading.Thread.Sleep(10);
                }

                Fingerprint Downloaded = new Fingerprint(file.RootPath, file.FileName + ".download");

                if (!Downloaded.Equals(file)) {
                    File.Delete(file.FullName + ".download");
                    string Msg = "Download error: " + file.FileName;

                    if (Downloaded.Size != file.Size) Msg += "\r\nSize mismatch (" + Downloaded.Size + " vs " + file.Size + ")";
                    if (Downloaded.Checksum != file.Checksum) Msg += "\r\nChecksum Mismatch (" + Downloaded.Checksum + " vs " + file.Checksum + ")";

                    if (file.Warn) m_ErrorLog.Add(Msg);
                    else m_WarningLog.Add(Msg);
                } else {
                    if (File.Exists(file.FullName))
                    {
                        File.SetAttributes(file.FullName, File.GetAttributes(file.FullName) & ~FileAttributes.ReadOnly);
                        File.Delete(file.FullName);
                    }

                    File.Move(file.FullName + ".download", file.FullName);
                    FlagVerified(file.FullName, file.Size, file.Checksum);
                }

                m_Downloaded += file.Size;
            }

            m_Status = "Done";
            m_current = "";
        }

        void DownloadFileComplete(object sender, AsyncCompletedEventArgs e) 
        {
            m_current = "";
            m_DownloadActive = false;
        }

        void dlProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            m_CurrDownloadBytes = e.BytesReceived;
            m_progress = (int)Math.Round(((float)(m_CurrDownloadBytes + m_Downloaded) / (float)m_DownloadSize) * 100.0f, 0);
        }


        public void DownloadManifest() {
            if (DontDownloadManifest) {

                ManifestDownloadComplete(null, null);
            }

            m_Status = "Fetching manifest...";
            LocalManifest = MyToolkit.ValidPath(Path.Combine(PathRoot, "rspatcher.xml"));
            client.StartDownload(new AsyncCompletedEventHandler(ManifestDownloadComplete),
                                new DownloadProgressChangedEventHandler(dlProgress),
                                ManifestURL,
                                LocalManifest);
        }

        void ManifestDownloadComplete(object sender, AsyncCompletedEventArgs e)
        {
            // Check if we had any HTTP download errors                     
            if (e != null) if (e.Error != null) {
                m_ErrorLog.Add("Manifest download error for " + ManifestURL +  "\r\n" + e.Error.Message);
                m_Status = "Done";
                return;
            }

            // Check if the downloaded file is where it should be           
            if (!File.Exists(LocalManifest))
            {
                m_ErrorLog.Add("Error downloading manifest");
                m_Status = "Done";
                return;
            }

            // Make certain the downloaded manifest and the one we          
            // requested match in size                                      
            FileInfo dlInfo = new FileInfo(LocalManifest);
            if (dlInfo.Length != client.Length) {
                m_ErrorLog.Add("Error downloading manifest");
                m_Status = "Done";
                return;
            } 

            // We got a manifest, lets start reading through it             
            m_current = "";

            m_ManifestFileList = new ArrayList();
            try
            {
                m_manifest = XElement.Load(LocalManifest);

                SelfPatch();
                
                m_Status = "Reading Manifest";
                IEnumerable<XElement> files = m_manifest.Descendants("file");
                    
                foreach (XElement file in files)
                {
                    if (Kill) return;

                    // Lets get this file's manifest information                                    
                    long size;
                    bool parseSucceed = long.TryParse(file.Attribute("size").Value.ToString(), out size); 
                    bool Warn = true;
                    if (file.Attribute("warn") != null)
                        if (file.Attribute("warn").Value == "no")
                            Warn = false;
                    string md5 = file.Attribute("md5").Value;
                    string fileName = file.Attribute("name").Value;

                    if (fileName.Trim() != "")
                    {
                        Fingerprint ManifestFingerprint = new Fingerprint(PathRoot, fileName, md5, size);
                        ManifestFingerprint.Warn = Warn;

                        IEnumerable<XElement> URLs = file.Descendants("url");

                        foreach (XElement URL in URLs)
                        {
                            ManifestFingerprint.AddDownloadURL(URL.Value.ToString().Trim());
                        }

                        m_ManifestFileList.Add(ManifestFingerprint);
                    }
                }

                m_progress = 0;
                m_Status = "Verifying";
                myWorkThread = new Thread(new ThreadStart(Validate));
                myWorkThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WorkThread.ManifestDownloadComplete()");
                string a = ex.Message;
            }
            
        }

        public void Cancel() {
            if (myWorkThread != null) if (myWorkThread.IsAlive) {
                try { myWorkThread.Abort(); }
                catch (Exception ex) { }
            }
        }

        void SelfPatch() {
            try
            {
                Fingerprint myFingerprint = new Fingerprint(Settings.GamePath, "Tequila.exe");

                if (GenerageChecksumToClipboard)
                {
                    Clipboard.SetText("md5=\"" + myFingerprint.Checksum + "\" size=" + myFingerprint.Size);
                }
                if (DontSelfUpdate) return;

                // Before we go far... lets see if there are any old temp files hanging around and get rid of them  

                string[] oldFiles = Directory.GetFiles(Settings.GamePath, "*.old");
                foreach (string oldFile in oldFiles) {
                    try { File.Delete(oldFile); }
                    catch (Exception ex) { }
                }

                // OK now thats out of the way, lets determine if we need to self patch or not!!                    
                IEnumerable<XElement> launchers = m_manifest.Descendants("launcher");
                m_Status = "Self Patching";
                foreach (XElement launcher in launchers)
                {
                    if (launcher.Attribute("id").Value == "tequila")
                    {

                        long size = 0;
                        long.TryParse(launcher.Attribute("size").Value.ToString(), out size);
                        string md5 = launcher.Attribute("md5").Value;

                        Fingerprint remoteLauncher = new Fingerprint(Settings.GamePath, "Tequila.exe", md5, size);

                        if (!myFingerprint.Equals(remoteLauncher))
                        {
                            // We need to update!!! yay...                                          
                            IEnumerable<XElement> urls = launcher.Descendants("url");

                            // Get every possible download URL into the remoteLauncher fingerprint  
                            foreach (XElement url in urls)
                                remoteLauncher.AddDownloadURL(url.Value);


                            // Start the download process                                           
                            HTTP selfPatcherClient = new HTTP();

                            m_DownloadSize = remoteLauncher.Size;
                            if (selfPatcherClient.StartDownload(new AsyncCompletedEventHandler(DownloadFileComplete),
                                                 new DownloadProgressChangedEventHandler(dlProgress),
                                                 remoteLauncher.DownloadURL,
                                                 remoteLauncher.FullName + ".download"))
                            {
                                m_Status = "Downloading";
                                m_DownloadActive = true;
                            }

                            m_current = remoteLauncher.FullName;

                            // Wait until download is complete                                      
                            while (selfPatcherClient.Active)
                            {
                                if (Kill) {
                                    selfPatcherClient.CancelDownload();
                                    return;
                                }
                                System.Threading.Thread.Sleep(10);
                            }
                            m_DownloadActive = false;

                            // Make sure the downloaded file is not corrupted                       

                            Fingerprint downloadedFile = new Fingerprint(remoteLauncher.RootPath, remoteLauncher.FileName + ".download");

                            if (!downloadedFile.Equals(remoteLauncher))
                            {
                                string error = "Was unable to self patch. Downloaded file did not match expected checksum.";
                                error += "\r\n" + remoteLauncher.FileName
                                      + "\r\n md5: " + remoteLauncher.Checksum + " vs " + downloadedFile.Checksum
                                      + "\r\n size: " + remoteLauncher.Size + " vs " + downloadedFile.Size;

                                File.Delete(downloadedFile.FullName + ".download");
                                m_ErrorLog.Add(error);
                                m_Status = "Done";
                                return;
                            }
                            else
                            {
                                // Find an available _#.old file name                                                                   
                                long i = 0;
                                string TrashName = myFingerprint.FullName + "_";
                                while (File.Exists(TrashName + i.ToString() + ".old")) i++;

                                TrashName = TrashName + i.ToString() + ".old";
                                File.Move(myFingerprint.FullName, TrashName);
                                File.Move(myFingerprint.FullName + ".download", myFingerprint.FullName);

                                var startInfo = new ProcessStartInfo();
                                startInfo.FileName = myFingerprint.FullName;
                                startInfo.Arguments = MyToolkit.AllArgs();

                                Process.Start(startInfo);

                                Application.Exit();
                                return;
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                MessageBox.Show(ex.Message, "WorkThread.SelfPatch()");
            }
        }
    }

    public class ProgressEventArgs : EventArgs { 
        private int m_progress;

        public ProgressEventArgs(long value, long max) {
            m_progress = (int)(Math.Round(value / max * 100.0f));
        }

        public int Progress
        {
            get { return m_progress; }
            set { m_progress = Progress; }
        }
    }
}
