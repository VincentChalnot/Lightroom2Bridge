using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Data;
using System.Data.SQLite;
using System.ComponentModel;
using System.Collections.ObjectModel;

namespace Lightroom2Bridge
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        private string lightroomCataloguePath;
        private ObservableCollection<AgCollection> collectionItems;

        public MainWindow()
        {
            InitializeComponent();
            this.collectionItems = new ObservableCollection<AgCollection>();
            this.sourceTextBox.Text = Properties.Settings.Default.lastUsedSource;
            string path = Properties.Settings.Default.bridgeCollectionPath;
            if (Directory.Exists(path))
            {
                this.bridgeCollectionFolderTextBox.Text = path;
            }
            else
            {
                this.resetBridgeCollectionsPath();
            }
            if (this.updateCollections())
            {
                try
                {
                    List<int> uncheckedItems = Properties.Settings.Default.uncheckedItems.Split(',').Select(s => Int32.Parse(s)).ToList();
                    foreach (int id in uncheckedItems)
                    {
                        this.collectionItems.Single(e => e.id == id).selected = false;
                    }
                }
                catch (Exception e)
                {
                    Console.Out.WriteLine("Error while loading unchecked items: " + e.Message);
                    Console.Out.WriteLine("Stored string was: " + Properties.Settings.Default.uncheckedItems);
                }
            }
        }

        private bool updateCollections()
        {
            this.lightroomCataloguePath = this.sourceTextBox.Text;
            string query = "SELECT c.id_local AS id, c.name AS name, p.name AS parent, COUNT(i.id_local) AS nbr FROM AgLibraryCollection c JOIN AgLibraryCollectionImage i ON (c.id_local = i.collection) LEFT JOIN AgLibraryCollection p ON (c.parent = p.id_local) GROUP BY c.id_local ORDER BY p.name, c.name";
            DataTable dt = new DataTable();
            try
            {
                this.databaseQuery(query, dt);
            }
            catch (Exception e)
            {
                this.disableSynchonization(e.Message);
                return false;
            }
            this.collectionItems.Clear();
            foreach (DataRow row in dt.Rows)
            {
                this.collectionItems.Add(new AgCollection()
                {
                    id = Convert.ToInt32(row["id"]),
                    selected = true,
                    parent = Convert.ToString(row["parent"]),
                    name = Convert.ToString(row["name"]),
                    count = Convert.ToInt32(row["nbr"]),
                });
            }
            this.collectionList.ItemsSource = this.collectionItems;
            Properties.Settings.Default.lastUsedSource = this.sourceTextBox.Text;
            Properties.Settings.Default.Save();
            this.synchronizeButton.IsEnabled = true;
            this.errorBlock.Visibility = Visibility.Hidden;
            return true;
        }

        private void disableSynchonization()
        {
            this.collectionItems.Clear();
            this.synchronizeButton.IsEnabled = false;
            Properties.Settings.Default.uncheckedItems = "";
            Properties.Settings.Default.Save();
        }

        private void disableSynchonization(String message)
        {
            this.errorBlock.Text = message;
            this.errorBlock.Visibility = Visibility.Visible;
            this.disableSynchonization();
        }

        private void resetBridgeCollectionsPath()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\Adobe\Bridge CS6\Collections";
            this.bridgeCollectionFolderTextBox.Text = path;
        }

        private void writeCollection(AgCollection collection, string filename)
        {
            // Console.Out.WriteLine("Writing : " + filename);
            string query = "SELECT r.absolutePath || d.pathFromRoot || f.originalFilename AS path FROM Adobe_images i JOIN AgLibraryCollectionImage c ON c.image = i.id_local JOIN AgLibraryFile f ON f.id_local = i.rootFile JOIN AgLibraryFolder d ON d.id_local = f.folder JOIN AgLibraryRootFolder r ON r.id_local = d.rootFolder WHERE c.collection = " + collection.id + " ORDER BY c.positionInCollection";
            DataTable dt = new DataTable();
            try
            {
                this.databaseQuery(query, dt);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "An error occured", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (System.IO.StreamWriter file = new System.IO.StreamWriter(filename))
            {
                file.WriteLine("<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>");
                file.WriteLine("<arbitrary_collection version='1'>");
                foreach (DataRow row in dt.Rows)
                {
                    string path = Convert.ToString(row["path"]);
                    path.Replace(" ", "%20");
                    file.WriteLine("<file uri='bridge:fs:file:///" + path + "'>");
                }
                file.WriteLine("</arbitrary_collection>");
            }
        }

        private void browseSource_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.DefaultExt = ".lrcat";
            dlg.Filter = "Lightroom Catalogue (*.lrcat)|*.lrcat";
            Nullable<bool> result = dlg.ShowDialog();
            if (result == true)
            {
                this.sourceTextBox.Text = dlg.FileName;
                this.updateCollections();
            }
        }

        private void sourceTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                this.updateCollections();
            }
        }

        private void bridgeCollectionBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
            System.Windows.Forms.DialogResult result = fbd.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                this.bridgeCollectionFolderTextBox.Text = fbd.SelectedPath;
            }
        }

        private void synchronizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.saveUncheckedItems();
            this.synchronizeButton.Content = "Synchronizing…";
            this.synchronizeButton.IsEnabled = false;
            this.synchroProgressBar.Maximum = this.collectionItems.Count();
            this.synchroProgressBar.Value = 1;
            this.synchroProgressBar.Visibility = Visibility.Visible;

            BackgroundWorker worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;

            string basePath = this.bridgeCollectionFolderTextBox.Text;
            bool overwrite = this.overwriteExistingCollectionsCheckbox.IsChecked == true;
            worker.DoWork += (obj, ev) => synchronizeCollections(obj, basePath, overwrite);
            worker.ProgressChanged += reportProgressChange;
            worker.RunWorkerCompleted += synchronizationCompleted;
            worker.RunWorkerAsync();
        }

        public void synchronizeCollections(object sender, string basePath, bool overwrite = true)
        {
            int i = 0;
            foreach (AgCollection collection in this.collectionItems)
            {
                if (collection.selected == true)
                {
                    string filename = basePath + "\\Lr - ";
                    if (collection.parent.Length > 0)
                    {
                        filename += collection.parent + " - ";
                    }
                    filename += collection.name + ".filelist";
                    if (!File.Exists(filename) || overwrite)
                    {
                        this.writeCollection(collection, filename);
                    }
                }
                (sender as BackgroundWorker).ReportProgress(i++);
            }

        }

        public void reportProgressChange(object sender, ProgressChangedEventArgs e)
        {
            this.synchroProgressBar.Value = e.ProgressPercentage;
        }

        public void synchronizationCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.synchronizeButton.IsEnabled = true;
            this.synchronizeButton.Content = "Synchronize";
            this.synchroProgressBar.Visibility = Visibility.Hidden;
            this.successLabel.Visibility = Visibility.Visible;
        }

        private void resetBridgeCollectionsPathButton_Click(object sender, RoutedEventArgs e)
        {
            this.resetBridgeCollectionsPath();
        }

        private void saveUncheckedItems()
        {
            List<string> disabledIds = new List<string>();
            foreach (AgCollection collection in this.collectionItems)
            {
                if (collection.selected != true)
                {
                    disabledIds.Add(Convert.ToString(collection.id));
                }
            }
            Properties.Settings.Default.uncheckedItems = String.Join(",", disabledIds);
            Properties.Settings.Default.Save();
        }

        private DataTable databaseQuery(string query, DataTable dt)
        {
            if (!File.Exists(this.lightroomCataloguePath))
            {
                throw new FileNotFoundException("File not found : " + this.lightroomCataloguePath);
            }
            try
            {
                SQLiteConnection con = new SQLiteConnection(String.Format("Data Source={0}", this.lightroomCataloguePath));
                con.Open();
                try
                {
                    SQLiteCommand cmd = new SQLiteCommand(con);
                    cmd.CommandText = query;
                    SQLiteDataAdapter da = new SQLiteDataAdapter(cmd);
                    da.Fill(dt);
                    da.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    con.Close();
                    throw new Exception("Can't load collections, please check that the source file is a Lightroom Catalogue", e);
                }
                con.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                throw new Exception("Can't open SQLite database, please check that the source file is a Lightroom Catalogue", e);
            }
            return dt;
        }

    }

    public class AgCollection
    {
        public int id { get; set; }
        public bool selected { get; set; }
        public string parent { get; set; }
        public string name { get; set; }
        public int count { get; set; }
    }
}
