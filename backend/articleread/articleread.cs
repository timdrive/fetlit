using System;
using System.Threading;
using System.Runtime.InteropServices;
using Oracle.DataAccess.Client;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Text;
using System.Web;
using System.Net;
using System.Web.Security;
using System.Security.Cryptography;

namespace FetlitCGI
{
    class Cgi
    {
        [DllImport("kernel32", SetLastError = true)]
        static extern int SetConsoleMode(int hConsoleHandle, int dwMode);

        private static int max_post_length = 5000000; // max length of posted text in bytes
        private static int max_con_timeout = 100; // max timeout of the connection milliseconds
        private static int max_num_stories = 10; // max number of stories per category to display

        private static string htmlRoot = "localhost";
        private static string htmlAction = "articleread.exe";
        private static string ALL_CATEGORIES = "ALL_CATEGORIES"; // This is a catch-all story category which will retrieve all categories

        private static string newStoryHash = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(System.Web.Security.Membership.GeneratePassword(32, 0))).Replace("=", "").Replace("+", "").Replace("/", "");

        //private static string PostData;
        private static int PostLength;
        private static char[] chararray;// http stream buffer

        private static EventLog eventLogger = (EventLog)null;
        private static string DateTimeFormatterForLog = "HH:mm:ss.fff";
        private static string DateFormatterForFileName = "yyyyMMdd";

        // Configuration Parameters
        private static string connectionString = "";
        private static string debugId = "";
        private static string processingPath = "";
        private static string workingPath = "";
        private static string archivePath = "";
        private static string errorPath = "";
        private static string logPath = "";
        private static bool authorOverwrite = false;
        private static bool articleOverwrite = false;


        public static void Go(EventLog el)
        {
            try
            {
                Cgi.connectionString = ConfigurationManager.ConnectionStrings["FetLit.Properties.Settings.DBConnectionString"].ToString();
                Cgi.debugId = ((object)ConfigurationManager.AppSettings["debug_id"]).ToString();
                Cgi.logPath = ((object)ConfigurationManager.AppSettings["log_dir"]).ToString();
                Cgi.processingPath = ((object)ConfigurationManager.AppSettings["processing_dir"]).ToString();
                Cgi.workingPath = ((object)ConfigurationManager.AppSettings["working_dir"]).ToString();
                Cgi.archivePath = ((object)ConfigurationManager.AppSettings["archive_dir"]).ToString();
                Cgi.errorPath = ((object)ConfigurationManager.AppSettings["error_dir"]).ToString();
                string overwrite = ((object)ConfigurationManager.AppSettings["author_overwrite"]).ToString();
                if (overwrite.ToLower().Equals("yes"))
                    authorOverwrite = true;
                overwrite = ((object)ConfigurationManager.AppSettings["article_overwrite"]).ToString();
                if (overwrite.ToLower().Equals("yes"))
                    articleOverwrite = true;
                Cgi.eventLogger = el;
                //ThreadPool.QueueUserWorkItem(new WaitCallback(Cgi.ThreadProc), (object)null);

            }
            catch (Exception ex)
            {
                Cgi.logError(((object)ex).ToString());
                throw new Exception(((object)ex).ToString());
            }
        }

        static string sha256(string rawstring)
        {
            System.Security.Cryptography.SHA256Managed crypt = new System.Security.Cryptography.SHA256Managed();
            System.Text.StringBuilder hash = new System.Text.StringBuilder();
            byte[] crypto = crypt.ComputeHash(Encoding.UTF8.GetBytes(rawstring), 0, Encoding.UTF8.GetByteCount(rawstring));
            foreach (byte theByte in crypto)
            {
                hash.Append(theByte.ToString("x2"));
            }
            return hash.ToString();
        }

        private static void logError(string msg)
        {
            string str = "[" + DateTime.Now.ToString(Cgi.DateTimeFormatterForLog) + "] ERROR: " + msg;
            TextWriter textWriter = (TextWriter)new StreamWriter(Cgi.logPath + "/ArticleReader_" + DateTime.Now.ToString(Cgi.DateFormatterForFileName) + ".log", true);
            textWriter.WriteLine(str);
            textWriter.Close();
        }

        private static void logError(string level, string msg)
        {
            string str = "[" + DateTime.Now.ToString(Cgi.DateTimeFormatterForLog) + "] " + level + ": " + msg;
            TextWriter textWriter = (TextWriter)new StreamWriter(Cgi.logPath + "/ArticleReader_" + DateTime.Now.ToString(Cgi.DateFormatterForFileName) + ".log", true);
            textWriter.WriteLine(str);
            textWriter.Close();
        }

        private static int getSQLRowCount(string sql)
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = sql;
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    num = 0;
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static int getNewStoryID()
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select seq_story_id.nextval from dual";
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    num = 0;
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static string getStarRating(double rating)
        {
            // hollow ☆       (&#9734;)
            // dot ✫          (&#10027;) 
            // semi-solid ✭   (&#10029;)
            // almost-solid ✮ (&#10030;)
            // solid ★        (&#9733;)
            // hollow-thin ✰  (&#10032;)
            // circle ✪       (&#10026;)
            /*
            if (rating > 0.00 && rating <= 1.00) return "★☆☆☆☆";

            else if (rating > 1.00 && rating <= 1.25) return "★✫☆☆☆";
            else if (rating > 1.25 && rating <= 1.50) return "★✭☆☆☆";
            else if (rating > 1.50 && rating <= 1.75) return "★✮☆☆☆";
            else if (rating > 1.75 && rating <= 2.00) return "★★☆☆☆";

            else if (rating > 2.00 && rating <= 2.25) return "★★✫☆☆";
            else if (rating > 2.25 && rating <= 2.50) return "★★✭☆☆";
            else if (rating > 2.50 && rating <= 2.75) return "★★✮☆☆";
            else if (rating > 2.75 && rating <= 3.00) return "★★★☆☆";

            else if (rating > 3.00 && rating <= 3.25) return "★★★✫☆";
            else if (rating > 3.25 && rating <= 3.50) return "★★★✭☆";
            else if (rating > 3.50 && rating <= 3.75) return "★★★✮☆";
            else if (rating > 3.75 && rating <= 4.00) return "★★★★☆";

            else if (rating > 4.00 && rating <= 4.25) return "★★★★✫";
            else if (rating > 4.25 && rating <= 4.50) return "★★★★✭";
            else if (rating > 4.50 && rating <= 4.75) return "★★★★✮";
            else if (rating > 4.75 && rating <= 5.00) return "★★★★★";

            else return "✪✪✪✪✪";
            */

            if (rating >= 0.00 && rating < 1.00) return "&#10032;&#10032;&#10032;&#10032;&#10032;";

            if (rating == 1.00) return "&#9733;";

            else if (rating > 1.00 && rating <= 1.25) return "&#9733;&#9734;";
            else if (rating > 1.25 && rating <= 1.50) return "&#9733;&#10029;";
            else if (rating > 1.50 && rating <= 1.75) return "&#9733;&#10030;";
            else if (rating > 1.75 && rating <= 2.00) return "&#9733;&#9733;";

            else if (rating > 2.00 && rating <= 2.25) return "&#9733;&#9733;&#9734;";
            else if (rating > 2.25 && rating <= 2.50) return "&#9733;&#9733;&#10029;";
            else if (rating > 2.50 && rating <= 2.75) return "&#9733;&#9733;&#10030;";
            else if (rating > 2.75 && rating <= 3.00) return "&#9733;&#9733;&#9733;";

            else if (rating > 3.00 && rating <= 3.25) return "&#9733;&#9733;&#9733;&#9734;";
            else if (rating > 3.25 && rating <= 3.50) return "&#9733;&#9733;&#9733;&#10029;";
            else if (rating > 3.50 && rating <= 3.75) return "&#9733;&#9733;&#9733;&#10030;";
            else if (rating > 3.75 && rating <= 4.00) return "&#9733;&#9733;&#9733;&#9733;";

            else if (rating > 4.00 && rating <= 4.25) return "&#9733;&#9733;&#9733;&#9733;&#9734;";
            else if (rating > 4.25 && rating <= 4.50) return "&#9733;&#9733;&#9733;&#9733;&#10029;";
            else if (rating > 4.50 && rating <= 4.75) return "&#9733;&#9733;&#9733;&#9733;&#10030;";
            else if (rating > 4.75 && rating <= 5.00) return "&#9733;&#9733;&#9733;&#9733;&#9733;";

            else return "&#10026;&#10026;&#10026;&#10026;&#10026;";

        }

        private static string getUserSecret(string author_name, string author_secret)
        {
            // this piece will attempt to retrieve the author secret based on the author name
            // if no author secret was found it will generate a new author secret
            // if the retrieved author secret does not match the provided author secret it will return "noauthorsecretfound"
            Random random = new Random();
            string existing_secret = "noauthorsecretfound";
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select user_secret from stories where upper(replace(replace(replace(replace(user_alias,' ',''),'.',''),'-',''),'_','')) = upper(replace(replace(replace(replace(:author_name,' ',''),'.',''),'-',''),'_','')) and rownum = 1";
                    command.Parameters.Add(new OracleParameter("author_name", author_name));
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    while (oracleDataReader.Read())
                    {
                        //Console.WriteLine(">>>>  " + oracleDataReader["user_secret"]);
                        if (DBNull.Value.Equals(oracleDataReader["user_secret"]))
                            existing_secret = "authorsecretnull";
                        else
                            existing_secret = Convert.ToString(oracleDataReader["user_secret"]);
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }

            // Logic for new authors. No such author (noauthorsecretfound) in the database, and author secret is empty
            if ((existing_secret.CompareTo("noauthorsecretfound")==0))
                return random.Next(1000, 9999).ToString();
            // Existing secret that matches the submitted author secret
            else if (existing_secret.CompareTo(author_secret) == 0)
                return existing_secret;
            else
                // the submitted author secret doesn't match the existing secret for the author 
                return "wrongauthorsecret";
        }

        private static string getStorySecret(string author_name, string story_name, string story_secret)
        {
            // this piece will attempt to retrieve the author secret based on the author name
            // if no author secret was found it will generate a new author secret
            // if the retrieved author secret does not match the provided author secret it will return "noauthorsecretfound"
            Random random = new Random();
            string existing_secret = "nostorysecretfound";
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select story_name, story_secret from stories where user_alias = :author_name and story_name = :story_name and rownum = 1";
                    command.Parameters.Add(new OracleParameter("author_name", author_name));
                    command.Parameters.Add(new OracleParameter("story_name", story_name));
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    while (oracleDataReader.Read())
                    {
                        //Console.WriteLine(">>>>  " + oracleDataReader["user_secret"]);
                        if (DBNull.Value.Equals(oracleDataReader["story_secret"]))
                            existing_secret = "storysecretnull";
                        else
                            existing_secret = Convert.ToString(oracleDataReader["story_secret"]);
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }

            // Logic for new authors. No such author (nostorysecretfound) in the database
            if ((existing_secret.CompareTo("nostorysecretfound") == 0))
                return random.Next(100000, 999999).ToString();
            // Existing secret that matches the submitted author secret
            else if (existing_secret.CompareTo(story_secret) == 0)
                return existing_secret;
            else
                // the submitted author secret doesn't match the existing secret for the author 
                return "wrongstorysecret";
        }

        private static string getStoryID(string author_name, string story_name, string story_secret)
        {
            // this piece will attempt to retrieve the author secret based on the author name
            // if no author secret was found it will generate a new author secret
            // if the retrieved author secret does not match the provided author secret it will return "noauthorsecretfound"
            Random random = new Random();
            string story_id = "0";
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select max(story_id) as story_id from stories where user_alias = :author_name and story_name = :story_name and story_secret = :story_secret";
                    command.Parameters.Add(new OracleParameter("author_name", author_name));
                    command.Parameters.Add(new OracleParameter("story_name", story_name));
                    command.Parameters.Add(new OracleParameter("story_secret", story_secret));
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    while (oracleDataReader.Read())
                    {
                            story_id = Convert.ToString(oracleDataReader["story_id"]);
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }

            return story_id;
        }

        private static int getNewCommentID()
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select seq_comment_id.nextval from dual";
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    num = 0;
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static int getNewRatingID()
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select seq_rating_id.nextval from dual";
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    num = 0;
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static int getFingerprintCheck(string fingerprint_hash)
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select count(*) as counts from ratings where fingerprint_hash = :fingerprint_hash and (sysdate-0.2) < created_date"; // check most recent ratings 
                    command.Parameters.Add(new OracleParameter("fingerprint_hash", fingerprint_hash));
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static int getFingerprintCheck(string fingerprint_hash, string story_id)
        {
            int num = -1;
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select count(*) as counts from ratings where fingerprint_hash = :fingerprint_hash and story_id = :story_id and (sysdate-0.2) < created_date"; // check most recent ratings 
                    command.Parameters.Add(new OracleParameter("fingerprint_hash", fingerprint_hash));
                    command.Parameters.Add(new OracleParameter("story_id", story_id));
                    OracleDataReader oracleDataReader = command.ExecuteReader();
                    while (oracleDataReader.Read())
                        num = (int)oracleDataReader.GetDecimal(0);
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return num;
        }

        private static string getStoryComments(string story_id)
        {
            string comments_table = "";
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;
                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = "select commenter_alias, comment_text, to_char(created_date,'DD-MON-YYYY') as comment_date from comments where LENGTH(comment_text) > 3 and story_id = :story_id order by created_date";

                    command.Parameters.Add(new OracleParameter("story_id", story_id));

                    OracleDataReader oracleDataReader = command.ExecuteReader();

                    while (oracleDataReader.Read())
                    {

                        comments_table += "<tr><td>";
                        comments_table += "<h3> By " + Convert.ToString(oracleDataReader["commenter_alias"]) + " on " + Convert.ToString(oracleDataReader["comment_date"]) + "</h3>";
                        comments_table += "<h3>" + Convert.ToString(oracleDataReader["comment_text"]) + "</h3>";
                        comments_table += "</tr></td>";

                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return comments_table;
        }

        private static Dictionary<string,string> getCategories()
        {
            string sql = "select code_short_name,code_long_name from system_control_codes where code_group = 'CATEGORY' and code_short_name <> '" + ALL_CATEGORIES + "'";
            Dictionary<string, string> dictCategories = new Dictionary<string,string>();
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;

                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = sql;
                    OracleDataReader oracleDataReader = command.ExecuteReader();

                    while (oracleDataReader.Read())
                    {
                        string category_code_short_name = Convert.ToString(oracleDataReader["code_short_name"]);
                        string category_code_long_name = Convert.ToString(oracleDataReader["code_long_name"]);
                        dictCategories.Add(category_code_short_name, category_code_long_name);
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return dictCategories;
        }

        private static string getStoryListByType(OracleCommand command2, string timespan, string code_short_name)
        {
            string story_list = "";
            string sql2 = "";

            if (code_short_name.CompareTo(ALL_CATEGORIES) == 0)
                sql2 = "select * from (select story_id,  user_alias, story_name, story_summary, to_char(created_date,'DD-MON-YYYY') as created_date, story_rating_overall, story_rating_plot, story_rating_grammar, story_rating_style, story_words, read_counter, (select count(*) from comments c where LENGTH(comment_text) > 3 and c.story_id = s.story_id) as comment_counter from stories s where 1=1 {1}) where ROWNUM <= {2}";
            else
                sql2 = "select * from (select story_id,  user_alias, story_name, story_summary, to_char(created_date,'DD-MON-YYYY') as created_date, story_rating_overall, story_rating_plot, story_rating_grammar, story_rating_style, story_words, read_counter, (select count(*) from comments c where LENGTH(comment_text) > 3 and c.story_id = s.story_id) as comment_counter from stories s where story_category_01 = '{0}' {1}) where ROWNUM <= {2}";

            string fob = " AND created_date >= SYSDATE-1 order by created_date desc";
            string day = " AND created_date BETWEEN to_date(to_char(SYSDATE-2,'DDMONYYYY'),'DDMONYYYY') AND to_date(to_char(SYSDATE-1,'DDMONYYYY'),'DDMONYYYY') order by created_date";
            string week = " AND created_date BETWEEN to_date(to_char(SYSDATE-7,'DDMONYYYY'),'DDMONYYYY') AND to_date(to_char(SYSDATE-1,'DDMONYYYY'),'DDMONYYYY') order by created_date";
            string month = " AND created_date BETWEEN to_date(to_char(SYSDATE-30,'DDMONYYYY'),'DDMONYYYY') AND to_date(to_char(SYSDATE-1,'DDMONYYYY'),'DDMONYYYY') order by story_rating_overall desc";
            string year = " AND created_date BETWEEN to_date(to_char(SYSDATE-365,'DDMONYYYY'),'DDMONYYYY') AND to_date(to_char(SYSDATE-1,'DDMONYYYY'),'DDMONYYYY') order by story_rating_overall desc";
            string ever = " order by story_rating_overall desc";
            string rand = " order by rowid";

            if (timespan.CompareTo("FOB") == 0) command2.CommandText = String.Format(sql2, code_short_name, fob, max_num_stories);
            else if (timespan.CompareTo("DAY") == 0) command2.CommandText = String.Format(sql2, code_short_name, day, max_num_stories);
            else if (timespan.CompareTo("WEEK")  == 0) command2.CommandText = String.Format(sql2, code_short_name, week, max_num_stories);
            else if (timespan.CompareTo("MONTH") == 0) command2.CommandText = String.Format(sql2, code_short_name, month, max_num_stories);
            else if (timespan.CompareTo("YEAR")  == 0) command2.CommandText = String.Format(sql2, code_short_name, year, max_num_stories);
            else if (timespan.CompareTo("EVER")  == 0) command2.CommandText = String.Format(sql2, code_short_name, ever, max_num_stories);
            else if (timespan.CompareTo("RANDOM")  == 0) command2.CommandText = String.Format(sql2, code_short_name, rand, max_num_stories);
            else command2.CommandText = String.Format(sql2, code_short_name, rand, 100);

            // this is override is for debug purposes only, remove lated
            //command2.CommandText = "select story_id, user_alias, story_name, story_summary, to_char(created_date,'DD-MON-YYYY') as created_date, story_rating_overall from stories where rownum < 3";

            //Console.WriteLine(command2.CommandText);

            logError("INFO", command2.CommandText);

            OracleDataReader oracleDataReader2 = command2.ExecuteReader();

            while (oracleDataReader2.Read())
            {
                string user_alias = Convert.ToString(oracleDataReader2["user_alias"]);
                string story_name = Convert.ToString(oracleDataReader2["story_name"]);
                string story_id = Convert.ToString(oracleDataReader2["story_id"]);
                string created_date = Convert.ToString(oracleDataReader2["created_date"]);
                string story_rating_overall = Convert.ToString(oracleDataReader2["story_rating_overall"]);
                string story_rating_plot = Convert.ToString(oracleDataReader2["story_rating_plot"]);
                string story_rating_grammar = Convert.ToString(oracleDataReader2["story_rating_grammar"]);
                string story_rating_style = Convert.ToString(oracleDataReader2["story_rating_style"]);
                string story_summary = Convert.ToString(oracleDataReader2["story_summary"]);
                string story_words = Convert.ToString(oracleDataReader2["story_words"]);
                string read_counter = Convert.ToString(oracleDataReader2["read_counter"]);
                string comment_counter = Convert.ToString(oracleDataReader2["comment_counter"]);

                story_list += "<tr>";
                story_list += "<td>";
                story_list += "<p class=\"i1\">"+getStarRating(Convert.ToDouble(story_rating_overall))+"</p>"; // hollow ☆ (&#9734;) semi-solid ✭ (&#10029;) almost-solid ✮ (&#10030;) solid ★ (&#9733;) hollow ✰ (& #10032;)


                string hot_beverage = "";
                string solid_heart = "";

                if (Convert.ToDouble(story_rating_overall) > 4.0 && Convert.ToDouble(read_counter) > 10)
                    hot_beverage = "&#9749;&nbsp";

                if ((Convert.ToDouble(story_rating_plot) + Convert.ToDouble(story_rating_grammar) + Convert.ToDouble(story_rating_style)) / 3 >= 4.66)
                    solid_heart = "&#9829;&nbsp";

                story_list += "<p class=\"i1\">" + hot_beverage + solid_heart + "<span class=\"si1b\">"; // hot beverage

                story_list += String.Format("{0:0.000}", Convert.ToDouble(story_rating_overall));
                story_list += "</span></p><p class=\"i3\">";
                story_list += created_date;
                story_list += "</p></td>";
                story_list += "<td><form action=\""+htmlAction+"\" method=\"POST\"><input type=\"hidden\" name=\"read_story_button\" value=\"" + story_id + "\"/><input type=\"submit\" value=\"";
                story_list += story_name;
                story_list += "\"/></form><h6>";
                story_list += story_summary;
                story_list += "</h6></td>";
                story_list += "<td><h5>";
                //<a href=\"";
                //story_list += "https://en.wikipedia.org/wiki/Anne_Desclos";
                //story_list += "\">";
                story_list += user_alias;
                //story_list += "</a>
                story_list += "</h5></td>";
                story_list += "<td>";
                story_list += String.Format("<p class=\"i3\">{0:###,###,###}</p>", Convert.ToDouble(story_words)); ;
                story_list += "</td>";
                story_list += "<td>";
                story_list += "<p class=\"i2\">&#9993; <span class=\"si2b\">" + comment_counter + "</span></p>"; // letter-email
                story_list += "<p class=\"i2\">&#8634; <span class=\"si2b\">" + read_counter + "</span></p>"; // reloads
                story_list += "<p class=\"i2\"><span class=\"si2b\">I-" + story_id + "</span></p>"; // story id
                story_list += "</td>";
                story_list += "</tr>";         
                
                //Console.WriteLine(">>>"+story_list);
            }

            oracleDataReader2.Close();
            oracleDataReader2.Dispose();

            return story_list;
        }

        private static string getStorySearch(Dictionary<string, string> postedStory)
        {
            logError("INFO", "getStorySearch: Start search...");
            string story_list = "";

            string searchsql = " select user_alias, story_name, story_id, "+
                " to_char(updated_date,'DD-MON-YYYY') as updated_date, story_rating_overall, story_rating_plot, " +
                " story_rating_grammar, story_rating_style, story_summary, "+
                " story_words, read_counter, "+
                " (select count(*) from comments c where LENGTH(comment_text) > 3 and c.story_id = s.story_id) as comment_counter "+
                " from stories s where ROWNUM <= " + max_num_stories;

            string htmlStoryTitle = postedStory["title"];
            string htmlStoryAuthorName = postedStory["author"];
            string htmlStoryFullBodySearch = postedStory["storybody"];
            string htmlStoryKeywords = postedStory["keywords"];
            string htmlStoryCategory = postedStory["category"];
            string htmlStoryLanguage = postedStory["language"];
            string htmlStoryFromDate = postedStory["fromdate"];
            string htmlStoryToDate = postedStory["todate"];
            string htmlStoryRating = postedStory["search_rating"];
            //string htmlStoryTagline = postedStory["tagline"];
            //string htmlStorySummary = postedStory["summary"];

             //" and get_clean_exactstring(story_line) LIKE get_clean_likestring(:story_name)" +
             //" and story_words >= :story_words" +;

            using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
            using (OracleCommand command = new OracleCommand(searchsql, oracleConnection) { BindByName = true })
            {
                try
                {

                    oracleConnection.Open();
                    
                    if (!(htmlStoryTitle.CompareTo("") == 0))
                    {
                        searchsql += " and get_clean_exactstring(story_name) LIKE get_clean_likestring(:story_name)";
                        command.Parameters.Add(new OracleParameter("story_name", htmlStoryTitle));
                        logError("INFO", searchsql + htmlStoryTitle);
                    }

                    if (!(htmlStoryAuthorName.CompareTo("") == 0))
                    {
                        searchsql += " and get_clean_exactstring(user_alias) LIKE get_clean_likestring(:user_alias)";
                        command.Parameters.Add(new OracleParameter("user_alias", htmlStoryAuthorName));
                        logError("INFO", searchsql + htmlStoryAuthorName);
                    }

                    if (!(htmlStoryFullBodySearch.CompareTo("") == 0))
                    {
                        searchsql += " and dbms_lob.instr(get_clean_exactclob(story_clob),get_clean_exactstring(:story_fullbodysearch))>0";
                        command.Parameters.Add(new OracleParameter("story_fullbodysearch", htmlStoryFullBodySearch));
                        logError("INFO", searchsql + htmlStoryFullBodySearch);
                    }

                    if (!(htmlStoryKeywords.CompareTo("") == 0))
                    {
                        searchsql += " and get_keyword_match(story_id,:story_keywords)>0";
                        command.Parameters.Add(new OracleParameter("story_keywords", htmlStoryKeywords));
                        logError("INFO", searchsql + htmlStoryKeywords);
                    }

                    if (!(htmlStoryToDate.CompareTo("") == 0 || htmlStoryFromDate.CompareTo("") == 0))
                    {
                        searchsql += " and updated_date between to_date(:fromdate,'yyyy-mm-dd') and TO_DATE(:todate,'yyyy-mm-dd')";
                        command.Parameters.Add(new OracleParameter("fromdate", htmlStoryFromDate));
                        command.Parameters.Add(new OracleParameter("todate", htmlStoryToDate));
                        logError("INFO", searchsql + htmlStoryFromDate + htmlStoryToDate);
                    }

                    if (!(htmlStoryCategory.CompareTo("ANY") == 0))
                    {
                        searchsql += " and story_category_01 = :story_category_01";
                        command.Parameters.Add(new OracleParameter("story_category_01", htmlStoryCategory));
                        logError("INFO", searchsql + htmlStoryCategory);
                    }

                    if (!(htmlStoryTitle.CompareTo("") == 0))
                    {
                        searchsql += " and story_rating_overall >= :story_rating_overall";
                        command.Parameters.Add(new OracleParameter("story_rating_overall", htmlStoryRating));
                        logError("INFO", searchsql + htmlStoryRating);
                    }

                    if (!(htmlStoryLanguage.CompareTo("ANY") == 0))
                    {
                        searchsql += " and story_language = :story_language";
                        command.Parameters.Add(new OracleParameter("story_language", htmlStoryLanguage));
                        logError("INFO", searchsql + htmlStoryLanguage);
                    }
                    
                    //command.Parameters.Add(new OracleParameter("story_line", htmlStoryTagline));
                    //command.Parameters.Add(new OracleParameter("story_summary", htmlStorySummary));
                    //command.Parameters.Add(new OracleParameter("story_words", "10000"));
                    //command.Parameters.Add(new OracleParameter("story_active", "Y"));
                    //command.Parameters.Add(new OracleParameter("story_status", "A"));
                    //command.Parameters.Add(new OracleParameter("story_notes", "some notes"));
                    
                    //command.Parameters.Add(new OracleParameter("story_rating_plot", "0"));
                    //command.Parameters.Add(new OracleParameter("story_rating_grammar", "0"));
                    //command.Parameters.Add(new OracleParameter("story_rating_style", "0"));

                    // and finally overwrite the original searchsql in case some of the filters were non-empty
                    command.CommandText = searchsql;

                    logError("INFO", "command.CommandText ... " + command.CommandText.ToString());

                    OracleDataReader oracleDataReader = command.ExecuteReader();

                    /////// Show again the basic story search body

                    Dictionary<string, string> dictCategories = getCategories();

                    Dictionary<string, string> dictLanguages = getLanguages();

                    string htmlStylesheet = "style.css";

                    Console.Write(getHtmlHead(htmlStylesheet));

                    Console.Write("<br />");

                    Console.Write("<div class=\"filter_story\">");
                    Console.Write("<form action=\"{0}\" method=\"POST\">", htmlAction);

                    Console.Write("<h2>Author pen name, wildcards allowed ( *mar*sad* ) : </h2><INPUT type=\"text\" NAME=\"author\" SIZE=\"100\" maxlength=\"42\" value=\"{0}\"/><br/><br/>", htmlStoryAuthorName);

                    Console.Write("<h2>Story title, wildcards allowed ( *sodom* ) : </h2><INPUT TYPE=\"text\" NAME=\"title\" SIZE=\"100\" maxlength=\"60\" value=\"{0}\"/><br/><br/>", htmlStoryTitle);

                    //Console.Write("<h2>Story tagline : </h2><INPUT TYPE=\"text\" NAME=\"tagline\" SIZE=\"100\" maxlength=\"90\" value=\"{0}\"/><br/><br/>");

                    Console.Write("<h2>Story keywords, comma separated ( xxx, holy, panties ) :</h2><INPUT TYPE=\"text\" NAME=\"keywords\" SIZE=\"100\" maxlength=\"90\" value=\"{0}\"/><br/><br/>", htmlStoryKeywords);

                    Console.Write("<h2>Story free text search, exact match, no wildcards :</h2><INPUT TYPE=\"text\" NAME=\"storybody\" SIZE=\"100\" maxlength=\"90\" value=\"{0}\"/><br/><br/>", htmlStoryFullBodySearch);

                    Console.Write("<h2>Story from date :&nbsp;&nbsp;<INPUT TYPE=\"date\" NAME=\"fromdate\" value=\"{0}\"/> mm/dd/yyyy<br/><br/></h2>", htmlStoryFromDate);

                    Console.Write("<h2>Story to date :&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;<INPUT TYPE=\"date\" NAME=\"todate\" value=\"{0}\"/> mm/dd/yyyy<br/><br/></h2>", htmlStoryToDate);

                    Console.Write("<h2>Story category :&nbsp;&nbsp;&nbsp;<select name=\"category\" id=\"category\">");
                    Console.Write(("<option value=\"ANY\" selected=\"selected\">Any Category</option>").Replace(htmlStoryCategory + "\">", htmlStoryCategory + "\" selected=\"selected\">"));
                    foreach (KeyValuePair<string, string> story_category in dictCategories)
                    {
                        Console.Write(("<option value=\"" + story_category.Key + "\">" + story_category.Value + "</option>").Replace(htmlStoryCategory + "\">", htmlStoryCategory + "\" selected=\"selected\">"));
                    }
                    Console.Write("</select><br /><br/></h2>");

                    Console.Write("");

                    Console.Write("<h2>Story language :&nbsp;&nbsp;<select name=\"language\" id=\"language\">");
                    Console.Write(("<option value=\"ANY\" selected=\"selected\">Any Language</option>").Replace(htmlStoryLanguage + "\">", htmlStoryLanguage + "\" selected=\"selected\">"));
                    foreach (KeyValuePair<string, string> story_language in dictLanguages)
                    {
                        Console.Write(("<option value=\"" + story_language.Key + "\">" + story_language.Value + "</option>").Replace(htmlStoryLanguage + "\">", htmlStoryLanguage + "\" selected=\"selected\">"));
                    }
                    Console.Write("</select><br/><br/></h2>");

                    Console.Write(("<h2>Rated at least :&nbsp;&nbsp;&nbsp;&nbsp;" +
                    "<select name=\"search_rating\" id=\"search_rating\">" +
                    "  <option value=\"1\">1 Star</option>" +
                    "  <option value=\"2\">2 Stars</option>" +
                    "  <option value=\"3\">3 Stars</option>" +
                    "  <option value=\"4\">4 Stars</option>" +
                    "  <option value=\"5\">5 Stars</option>" +
                    "</select><br /><br /></h2>").Replace("value=\"" + htmlStoryRating + "\">", "value=\"" + htmlStoryRating + "\" selected=\"selected\">"));

                    Console.Write("<h2><button type=\"submit\" name=\"filter_story_button\" value=\"filter_story\">Search Stories</button><br/><br/></h2>");

                    Console.Write("</form>");
                    Console.Write("</div><div><table class=\"pure-table\"><thead><tr><th>rating</th><th>title / summary</th><th>author</th><th>length</th><th>flare</th></tr></thead><tbody>");

                    ////////////

                    while (oracleDataReader.Read())
                    {

                        string user_alias = Convert.ToString(oracleDataReader["user_alias"]);
                        string story_name = Convert.ToString(oracleDataReader["story_name"]);
                        string story_id = Convert.ToString(oracleDataReader["story_id"]);
                        string created_date = Convert.ToString(oracleDataReader["updated_date"]);
                        string story_rating_overall = Convert.ToString(oracleDataReader["story_rating_overall"]);
                        string story_rating_plot = Convert.ToString(oracleDataReader["story_rating_plot"]);
                        string story_rating_grammar = Convert.ToString(oracleDataReader["story_rating_grammar"]);
                        string story_rating_style = Convert.ToString(oracleDataReader["story_rating_style"]);
                        string story_summary = Convert.ToString(oracleDataReader["story_summary"]);
                        string story_words = Convert.ToString(oracleDataReader["story_words"]);
                        string read_counter = Convert.ToString(oracleDataReader["read_counter"]);
                        string comment_counter = Convert.ToString(oracleDataReader["comment_counter"]);

                        story_list += "<tr>";
                        story_list += "<td>";
                        story_list += "<p class=\"i1\">" + getStarRating(Convert.ToDouble(story_rating_overall)) + "</p>"; // hollow ☆ (&#9734;) semi-solid ✭ (&#10029;) almost-solid ✮ (&#10030;) solid ★ (&#9733;) hollow ✰ (& #10032;)


                        string hot_beverage = "";
                        string solid_heart = "";

                        if (Convert.ToDouble(story_rating_overall) > 4.0 && Convert.ToDouble(read_counter) > 10)
                            hot_beverage = "&#9749;&nbsp";

                        if ((Convert.ToDouble(story_rating_plot) + Convert.ToDouble(story_rating_grammar) + Convert.ToDouble(story_rating_style)) / 3 >= 4.66)
                            solid_heart = "&#9829;&nbsp";

                        story_list += "<p class=\"i1\">" + hot_beverage + solid_heart + "<span class=\"si1b\">"; // hot beverage

                        story_list += String.Format("{0:0.000}", Convert.ToDouble(story_rating_overall));
                        story_list += "</span></p><p class=\"i3\">";
                        story_list += created_date;
                        story_list += "</p></td>";
                        story_list += "<td><h2><form action=\"" + htmlAction + "\" method=\"POST\"><input type=\"hidden\" name=\"read_story_button\" value=\"" + story_id + "\"/><input type=\"submit\" value=\"";
                        story_list += story_name;
                        story_list += "\"/></form></h2><h3>";
                        story_list += story_summary;
                        story_list += "</h3></td>";
                        story_list += "<td><h3>";
                        story_list += user_alias;
                        story_list += "</h3></td>";
                        story_list += "<td>";
                        story_list += String.Format("<p class=\"i3\">{0:###,###,###}</p>", Convert.ToDouble(story_words)); ;
                        story_list += "</td>";
                        story_list += "<td>";
                        story_list += "<p class=\"i2\">&#9993; <span class=\"si2b\">" + comment_counter + "</span></p>"; // letter-email
                        story_list += "<p class=\"i2\">&#8634; <span class=\"si2b\">" + read_counter + "</span></p>"; // reloads
                        story_list += "<p class=\"i2\"><span class=\"si2b\">I-" + story_id + "</span></p>"; // story id
                        story_list += "</td>";
                        story_list += "</tr>";

                    }

                    Console.Write(story_list);

                    Console.Write("</body></html>");

                    command.Parameters.Clear();
                    command.Connection.Close();
                    command.Dispose();

                }
                catch (Exception ex)
                {
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }


            return story_list;
        }

        private static string getStoryTable()
        {

            int category_counter = 1;

            
            string sql = "select code_short_name,code_description from system_control_codes where code_group = 'CATEGORY'";

            string tabordion_div = "<div class=\"tabordion\">";
            string tabordion_section = "<section id=\"section{0}\"><input type=\"radio\" name=\"sections\" id=\"option{0}\" {2}><label for=\"option{0}\">{1}"; // {0} cycle over the categories, {1} string category label, {2} checked
            string tabordion_article = "</label><article><h2>{0}</h2>";// string article name

            string tabordion_h_div = "<div class=\"tabordion_h\">";
            string tabordion_h_section = "<section id=\"section{0}_{1}\"><input type=\"radio\" name=\"sections_h\" id=\"option{0}_{1}\" {3}><label for=\"option{0}_{1}\">{2}"; // {0}cycle over categories, {1} horizontal tabs, {2} string table label, {3} checked
            
            string tabordion_table_header = "</label><article><table class=\"pure-table\"><thead><tr><th>rating</th><th>title / summary</th><th>author</th><th>length</th><th>flare</th></tr></thead><tbody>";

            string tabordion_table_close = "</tbody></table></article></section>";

            string tabordion_h_section_close = "</div></article></section>";

            string html_close = "</div></body></html>";

            string storytable = getHtmlHead("style.css");
 
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                using (OracleConnection oracleConnection2 = new OracleConnection())
                {
                    try
                    {
                        oracleConnection.ConnectionString = Cgi.connectionString;
                        oracleConnection2.ConnectionString = Cgi.connectionString;

                        oracleConnection.Open();
                        oracleConnection2.Open();

                        OracleCommand command = oracleConnection.CreateCommand();
                        OracleCommand command2 = oracleConnection2.CreateCommand();

                        command.CommandText = sql;

                        OracleDataReader oracleDataReader = command.ExecuteReader();

                        storytable += tabordion_div;

                        while (oracleDataReader.Read())
                        {

                            string code_short_name = Convert.ToString(oracleDataReader["code_short_name"]);
                            string code_description = Convert.ToString(oracleDataReader["code_description"]);

                            storytable += String.Format(tabordion_section, category_counter, code_short_name, (category_counter == 1) ? "checked" : "");
                            storytable += String.Format(tabordion_article, code_description);

                            storytable += tabordion_h_div;

                            storytable += String.Format(tabordion_h_section, category_counter, 1, "FOB", (category_counter == 1) ? "checked" : "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "FOB", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 2, "DAY","") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "DAY", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 3, "WEEK", "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "WEEK", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 4, "MONTH", "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "MONTH", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 5, "YEAR", "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "YEAR", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 6, "EVER", "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "EVER", code_short_name) + tabordion_table_close;
                            storytable += String.Format(tabordion_h_section, category_counter, 7, "RANDOM", "") + tabordion_table_header;
                            storytable += getStoryListByType(command2, "RANDOM", code_short_name) + tabordion_table_close;

                            storytable += tabordion_h_section_close;

                            category_counter++;
                        }

                        storytable += html_close;

                        Console.WriteLine(storytable);

                        oracleDataReader.Close();
                        command.Dispose();
                        command2.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Cgi.logError(((object)ex).ToString());
                        throw new Exception(((object)ex).ToString());
                    }
                    finally
                    {
                        oracleConnection.Close();
                        oracleConnection.Dispose();
                        oracleConnection2.Close();
                        oracleConnection2.Dispose();
                    }
                }
            }
            return storytable;
        }

        private static Dictionary<string,string> getLanguages()
        {
            string sql = "select code_short_name,code_long_name from system_control_codes where code_group = 'LANGUAGE'";
            Dictionary<string, string> dictLanguages = new Dictionary<string,string>();
            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;

                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = sql;
                    OracleDataReader oracleDataReader = command.ExecuteReader();

                    while (oracleDataReader.Read())
                    {
                        string language_code_short_name = Convert.ToString(oracleDataReader["code_short_name"]);
                        string language_code_long_name = Convert.ToString(oracleDataReader["code_long_name"]);
                        dictLanguages.Add(language_code_short_name, language_code_long_name);
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return dictLanguages;
        }

        private static string GetCommandLogString(OracleCommand command)
        {
            string outputText;

            if (command.Parameters.Count == 0)
            {
                outputText = command.CommandText;
            }
            else
            {
                StringBuilder output = new StringBuilder();
                output.Append(command.CommandText);
                output.Append("; ");

                OracleParameter p;
                int count = command.Parameters.Count;
                for (int i = 0; i < count; i++)
                {
                    p = (OracleParameter)command.Parameters[i];
                    output.Append(string.Format("{0} = '{1}'", p.ParameterName, p.Value));
                    //output.Append(string.Format("'{0}'", p.Value));
                    if (i + 1 < count)
                    {
                        output.Append(", ");
                    }
                }
                outputText = output.ToString();
            }
            return outputText;
        }

        private static bool insertStory(Dictionary<string, string> postedStory)
        {

            string storycolumns =
                "story_id, user_alias, user_secret, story_name," +
                "story_line, story_keywords, story_copyright, story_summary, story_clob," +
                "story_words, story_active, story_status, story_notes, story_language," +
                "story_category_01, story_category_02, story_category_03," +
                "story_rating_overall, story_rating_plot," +
                "story_rating_grammar, story_rating_style," +
                "story_secret, created_date, updated_date";

            string storyparams =
                ":story_id, :user_alias, :user_secret, :story_name," +
                ":story_line, :story_keywords, :story_copyright, :story_summary, EMPTY_CLOB()," +
                ":story_words, :story_active, :story_status, :story_notes, :story_language," +
                ":story_category_01, :story_category_02, :story_category_03," +
                ":story_rating_overall, :story_rating_plot," +
                ":story_rating_grammar, :story_rating_style," +
                ":story_secret, SYSDATE, SYSDATE";

            string htmlStoryTitle = postedStory["title"];
            string htmlStoryAuthorName = postedStory["author"];
            string htmlStoryAuthorEmail = postedStory["email"];
            string htmlStoryAuthorSecret = postedStory["asecret"];
            string htmlStorySecret = postedStory["ssecret"];
            string htmlStoryTagline = postedStory["tagline"];
            string htmlStorySummary = postedStory["summary"];
            string htmlStoryCleanBody = postedStory["content"];
            string htmlStoryPostBody = postedStory["raw_story"];
            string htmlStoryCategory = postedStory["category"];
            string htmlStoryLanguage = postedStory["language"];
            string htmlStoryKeywords = postedStory["keywords"];

            int story_id = getNewStoryID();

            var insertstorysql = "insert into stories (" + storycolumns + ") values (" + storyparams + ")";
            string updatestorysql = "update stories set story_clob = :2 where story_id = :1";

            //Console.WriteLine(">>> About to open connection...");

            logError("INFO", "insertStory: Begin story insert...");

            using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
            using (OracleCommand command = new OracleCommand(insertstorysql, oracleConnection) { BindByName = true } )
            {
                try
                {

                    //Console.WriteLine(">>> command.Parameters.Add...");

                    // THE ADD ORDER MUST FOLLOW THE ORDER OF THE COLUMNS ABOVE UNLESS BindByName = true
                    command.Parameters.Add(new OracleParameter("story_id", story_id));
                    command.Parameters.Add(new OracleParameter("user_alias", htmlStoryAuthorName));
                    command.Parameters.Add(new OracleParameter("user_secret", htmlStoryAuthorSecret));
                    command.Parameters.Add(new OracleParameter("story_name", htmlStoryTitle));
                    command.Parameters.Add(new OracleParameter("story_line", htmlStoryTagline));
                    command.Parameters.Add(new OracleParameter("story_keywords", htmlStoryKeywords));
                    command.Parameters.Add(new OracleParameter("story_copyright", "Copyright (c) " + htmlStoryAuthorName));
                    command.Parameters.Add(new OracleParameter("story_summary", htmlStorySummary));
                    command.Parameters.Add(new OracleParameter("story_words", "10000"));
                    command.Parameters.Add(new OracleParameter("story_active", "Y"));
                    command.Parameters.Add(new OracleParameter("story_status", "A"));
                    command.Parameters.Add(new OracleParameter("story_notes", "some notes"));
                    command.Parameters.Add(new OracleParameter("story_language", htmlStoryLanguage));
                    command.Parameters.Add(new OracleParameter("story_category_01", htmlStoryCategory));
                    command.Parameters.Add(new OracleParameter("story_category_02", htmlStoryCategory));
                    command.Parameters.Add(new OracleParameter("story_category_03", htmlStoryCategory));
                    command.Parameters.Add(new OracleParameter("story_rating_overall", "0"));
                    command.Parameters.Add(new OracleParameter("story_rating_plot", "0"));
                    command.Parameters.Add(new OracleParameter("story_rating_grammar", "0"));
                    command.Parameters.Add(new OracleParameter("story_rating_style", "0"));
                    command.Parameters.Add(new OracleParameter("story_secret", htmlStorySecret));

                    oracleConnection.Open();

                    command.ExecuteNonQuery();

                    logError("INFO", "insertStory: End story insert... begin clob update...");

                    /* Portion to update the CLOB story */
                    command.Parameters.Clear();
                    command.CommandText = updatestorysql;

                    command.Parameters.Add(new OracleParameter("1", story_id));
                    command.Parameters.Add("2", OracleDbType.Clob, htmlStoryCleanBody, System.Data.ParameterDirection.Input);

                    command.ExecuteNonQuery();
                    /* End portion to update the CLOB story */

                    command.Connection.Close();
                    command.Dispose();

                    logError("INFO", "insertStory: End clob update...");

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return true;
        }

        private static bool updateStory(Dictionary<string, string> postedStory)
        {

            string story_id = postedStory["story_id"];
            string htmlStoryTitle = postedStory["title"];
            string htmlStoryAuthorName = postedStory["author"];
            string htmlStoryAuthorEmail = postedStory["email"];
            string htmlStoryAuthorSecret = postedStory["asecret"];
            string htmlStorySecret = postedStory["ssecret"];
            string htmlStoryTagline = postedStory["tagline"];
            string htmlStorySummary = postedStory["summary"];
            string htmlStoryCleanBody = postedStory["content"];
            string htmlStoryPostBody = postedStory["raw_story"];
            string htmlStoryCategory = postedStory["category"];
            string htmlStoryLanguage = postedStory["language"];
            string htmlStoryKeywords = postedStory["keywords"];

            string updatestorysql = "update stories set story_line = :story_line, story_summary = :story_summary, story_keywords = :story_keywords, story_language = :story_language, story_category_01 = :story_category_01, story_clob = :story_clob where story_id = :story_id";

            //Console.WriteLine(">>> About to open connection...");

            using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
            using (OracleCommand command = new OracleCommand(updatestorysql, oracleConnection) { BindByName = true })
            {
                try
                {

                    // THE ADD ORDER MUST FOLLOW THE ORDER OF THE COLUMNS ABOVE UNLESS BindByName = true

                    command.CommandText = updatestorysql;

                    command.Parameters.Add(new OracleParameter("story_id", story_id));
                    command.Parameters.Add(new OracleParameter("story_line", htmlStoryTagline));
                    command.Parameters.Add(new OracleParameter("story_summary", htmlStorySummary));
                    command.Parameters.Add(new OracleParameter("story_keywords", htmlStoryKeywords));
                    command.Parameters.Add(new OracleParameter("story_language", htmlStoryLanguage));
                    command.Parameters.Add(new OracleParameter("story_category_01", htmlStoryCategory));
                    command.Parameters.Add("story_clob", OracleDbType.Clob, htmlStoryCleanBody, System.Data.ParameterDirection.Input);

                    oracleConnection.Open();
                    command.ExecuteNonQuery();
                    command.Connection.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return true;
        }

        private static bool incStoryReadCounter(string story_id)
        {

            string updatestorysql = "update stories set read_counter = read_counter+1 where story_id = :1";

            using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
            using (OracleCommand command = new OracleCommand(updatestorysql, oracleConnection) { BindByName = true })
            {
                try
                {

                    command.Parameters.Add(new OracleParameter("1", story_id));

                    oracleConnection.Open();

                    command.ExecuteNonQuery();

                    command.Connection.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }
            return true;
        }

        private static int commentStory(Dictionary<string, string> postedAction, string fingerprint_hash)
        {

            string newcommentsql = "insert into comments (comment_id, story_id, user_id, commenter_alias, comment_text, created_date, updated_date) values (:comment_id, :story_id, :user_id, :commenter_alias, :comment_text, SYSDATE, SYSDATE)";

            if (getFingerprintCheck(fingerprint_hash, postedAction["story_id"]) <= 0)
            {

                int comment_id = getNewCommentID();

                using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
                using (OracleCommand command = new OracleCommand(newcommentsql, oracleConnection) { BindByName = true })
                {
                    try
                    {

                        command.Parameters.Add(new OracleParameter("comment_id", comment_id));
                        command.Parameters.Add(new OracleParameter("story_id", postedAction["story_id"]));
                        command.Parameters.Add(new OracleParameter("user_id", "0"));
                        command.Parameters.Add(new OracleParameter("commenter_alias", "Anonymous"));
                        command.Parameters.Add(new OracleParameter("comment_text", postedAction["comment"] + " "));

                        oracleConnection.Open();

                        command.ExecuteNonQuery();

                        command.Connection.Close();
                        command.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                        Cgi.logError(((object)ex).ToString());
                        throw new Exception(((object)ex).ToString());
                    }
                    finally
                    {
                        oracleConnection.Close();
                        oracleConnection.Dispose();
                    }
                }
                return comment_id;
            }
            else
            {
                return 0;
            }
        }

        private static bool rateStory(Dictionary<string, string> postedAction, int comment_id, string fingerprint_hash)
        {

            string columns = 
                "rating_id,story_id,comment_id,user_id,rating_overall, rating_plot,"+
                "rating_grammar,rating_style,REMOTE_ADDR, HTTP_USER_AGENT," +
                "HTTP_ACCEPT,HTTP_ACCEPT_ENCODING, HTTP_ACCEPT_LANGUAGE," +
                "CONTENT_TYPE, fingerprint_hash, created_date, updated_date";
            
            string parameters =
                ":rating_id,:story_id,:comment_id,:user_id,:rating_overall,:rating_plot,"+
                ":rating_grammar,:rating_style,:REMOTE_ADDR,:HTTP_USER_AGENT," +
                ":HTTP_ACCEPT,:HTTP_ACCEPT_ENCODING,:HTTP_ACCEPT_LANGUAGE," +
                ":CONTENT_TYPE, :fingerprint_hash";

            string newratingsql = "insert into ratings ("+columns+") values ("+parameters+", SYSDATE, SYSDATE)";

            string updatestoryratingsql = "UPDATE STORIES SET story_rating_overall = " +
                "(SELECT ROUND(AVG(RATING_BY_USER),3) AS AVERAGE_RATING FROM ( " +
                "SELECT story_id, AVG(rating_overall) AS RATING_BY_USER, fingerprint_hash FROM ratings " +
                "WHERE STORY_ID = " + postedAction["story_id"] + " GROUP BY story_id,fingerprint_hash)) " +
                "WHERE STORY_ID = " + postedAction["story_id"];

            if (getFingerprintCheck(fingerprint_hash, postedAction["story_id"]) <= 0)
            {
                int rating_id = getNewRatingID();

                using (OracleConnection oracleConnection = new OracleConnection(Cgi.connectionString))
                using (OracleCommand command = new OracleCommand(newratingsql, oracleConnection) { BindByName = true })
                {
                    try
                    {

                        command.Parameters.Add(new OracleParameter("rating_id", rating_id));
                        command.Parameters.Add(new OracleParameter("story_id", postedAction["story_id"]));
                        command.Parameters.Add(new OracleParameter("comment_id", comment_id));
                        command.Parameters.Add(new OracleParameter("user_id", "0"));
                        command.Parameters.Add(new OracleParameter("rating_overall", postedAction["rating"]));
                        command.Parameters.Add(new OracleParameter("rating_plot", ""));
                        command.Parameters.Add(new OracleParameter("rating_grammar", ""));
                        command.Parameters.Add(new OracleParameter("rating_style", ""));

                        command.Parameters.Add(new OracleParameter("REMOTE_ADDR", postedAction["REMOTE_ADDR"]));
                        command.Parameters.Add(new OracleParameter("HTTP_USER_AGENT", postedAction["HTTP_USER_AGENT"]));
                        command.Parameters.Add(new OracleParameter("HTTP_ACCEPT", postedAction["HTTP_ACCEPT"]));
                        command.Parameters.Add(new OracleParameter("HTTP_ACCEPT_ENCODING", postedAction["HTTP_ACCEPT_ENCODING"]));
                        command.Parameters.Add(new OracleParameter("HTTP_ACCEPT_LANGUAGE", postedAction["HTTP_ACCEPT_LANGUAGE"]));
                        command.Parameters.Add(new OracleParameter("CONTENT_TYPE", postedAction["CONTENT_TYPE"]));
                        command.Parameters.Add(new OracleParameter("fingerprint_hash",
                            sha256(
                            postedAction["REMOTE_ADDR"] +
                            postedAction["HTTP_USER_AGENT"] +
                            postedAction["HTTP_ACCEPT"] +
                            postedAction["HTTP_ACCEPT_ENCODING"] +
                            postedAction["HTTP_ACCEPT_LANGUAGE"] +
                            postedAction["CONTENT_TYPE"]
                            )));

                        oracleConnection.Open();

                        command.ExecuteNonQuery();

                        /* Portion to update the rating of the story */
                        command.Parameters.Clear();
                        command.CommandText = updatestoryratingsql;
                        command.ExecuteNonQuery();
                        /* End portion to update the rating of the story */

                        command.Connection.Close();
                        command.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                        Cgi.logError(((object)ex).ToString());
                        throw new Exception(((object)ex).ToString());
                    }
                    finally
                    {
                        oracleConnection.Close();
                        oracleConnection.Dispose();
                    }
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        public static string Sanitize(string Raw)
        {
            string Clean = "";

            logError("INFO", "Sanitize: Begin story sanitize...");

            if (Raw == null) return Clean;
            Raw = Raw.Replace("%22", "\"");                                                                     //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%E2%80%94", "—");                                                                //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%0D%0A", "\n");                                                                  //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%0D", "\n");                                                                     //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%0A", "\n");                                                                     //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%27", "'");                                                                      //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&#44;", ","); Raw = Raw.Replace("%2C", ",");                                     //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&#34;", "\""); Raw = Raw.Replace("&quot;", "\""); Raw = Raw.Replace("%5C", "\\"); //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&#60;", "<"); Raw = Raw.Replace("&lt;", "<"); Raw = Raw.Replace("+", " "); Raw = Raw.Replace("%3C", "<"); //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&#62;", ">"); Raw = Raw.Replace("&gt;", ">"); Raw = Raw.Replace("3E", ">");      //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&#160;", " "); Raw = Raw.Replace("&nbsp;", " ");                                 //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%21", "!"); Raw = Raw.Replace("%40", "@"); Raw = Raw.Replace("%23", "#");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%24", "$"); Raw = Raw.Replace("%5E", "^"); Raw = Raw.Replace("%28", "(");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%29", ")"); Raw = Raw.Replace("%25", "%"); Raw = Raw.Replace("%2F", "/");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%3F", "?"); Raw = Raw.Replace("%3B", ";"); Raw = Raw.Replace("%3A", ":");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%5D", "]"); Raw = Raw.Replace("%5B", "["); Raw = Raw.Replace("%7D", "}");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%7B", "{"); Raw = Raw.Replace("%7C", "|"); //Raw = Raw.Replace("%3D", "=");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("%2B", "+"); Raw = Raw.Replace("%7E", "~"); Raw = Raw.Replace("%60", "`");        //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Raw = Raw.Replace("&amp;", "&"); Raw = Raw.Replace("&#38;", "&"); //Raw = Raw.Replace("%26", "^");    //Console.WriteLine("22 >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            logError("INFO", "Sanitize: End story sanitize...");

            /*
            
            int Walk;
            char[] ByCharacter;
            ByCharacter = Raw.ToCharArray();

            for (Walk = 0; Walk < Raw.Length; Walk++)
            {
                
                switch (ByCharacter[Walk])
                {
                    case '\'': Clean += "'"; break;
                    case '\n': Clean += "\n"; break;
                    case '\r': Clean += "\n"; break;
                    case '\t': Clean += "\n"; break;
                    case '\v': Clean += "\n"; break;
                    case '(': Clean += ")"; break;
                    case ')': Clean += "("; break;
                    case '?': Clean += "?"; break;
                    case '!': Clean += "!"; break;
                    case ';': Clean += ";"; break;
                    case ':': Clean += ":"; break;
                    case '-': Clean += "-"; break;
                    case '—': Clean += "—"; break;
                    case '*': Clean += "*"; break;
                    case '"': Clean += "\""; break;
                    case '’': Clean += "'"; break;
                    case ' ': Clean += " "; break;
                    case '&': Clean += "&"; break;

                    default:
                        {
                            if (ByCharacter[Walk] >= 'A' && ByCharacter[Walk] <= 'z' ||
                            ByCharacter[Walk] >= '0' && ByCharacter[Walk] <= '9' ||
                            ByCharacter[Walk] == '=' || ByCharacter[Walk] == ',' ||
                            ByCharacter[Walk] == '.' || ByCharacter[Walk] == '@' ||
                            ByCharacter[Walk] == '#')
                                Clean += ByCharacter[Walk].ToString();
                            else Clean += "^";
                        }; break;
                }
                
                Clean += ByCharacter[Walk].ToString();
            }
            */
            //Console.WriteLine("Walk >>> " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            Clean = Raw;

            return Clean;
        }  // End of sanitize() method.

        public static string PostSanitize(string Raw)
        {

            if (Raw == null) return "";
            Raw = Raw.Replace("%3D", "=");
            Raw = Raw.Replace("%26", "&");

            return Raw;

        }  // End of PostSanitize() method.

        private static void GatherPostThread()
        {
            if (PostLength > max_post_length) { PostLength = max_post_length; }  // Max length for POST data for security.

            Array.Resize(ref chararray, PostLength);

            int i = 0;
            for (; PostLength > 0; PostLength--)
            {
                //PostData += Convert.ToChar(Console.Read()).ToString();
                chararray[i++] = Convert.ToChar(Console.Read());
            }
        }

        private static Dictionary<string, string> postedStoryParse()
        {
            logError("INFO", "postedStoryParse: Begin reading...");

            Dictionary<string, string> pStory = new Dictionary<string, string>();

            ThreadStart ThreadDelegate = new ThreadStart(GatherPostThread);
            Thread PostThread = new Thread(ThreadDelegate);
            PostLength = Convert.ToInt32(System.Environment.GetEnvironmentVariable("CONTENT_LENGTH"));

            ////// GET ALL REMOTE ENVIRONEMNT VARIABLES

            //The remote hostname of the user making the request
            pStory.Add("REMOTE_HOST", System.Environment.GetEnvironmentVariable("REMOTE_HOST"));
            //The remote IP address of the user making the request
            pStory.Add("REMOTE_ADDR", System.Environment.GetEnvironmentVariable("REMOTE_ADDR"));
            //The authenticated name of the user
            pStory.Add("REMOTE_USER", System.Environment.GetEnvironmentVariable("REMOTE_USER"));
            //The browser the client is using to issue the request
            pStory.Add("HTTP_USER_AGENT", System.Environment.GetEnvironmentVariable("HTTP_USER_AGENT"));
            //A list of the MIME types that the client can accept
            pStory.Add("HTTP_ACCEPT", System.Environment.GetEnvironmentVariable("HTTP_ACCEPT"));
            //A list of the HTTP_ACCEPT_ENCODING types that the client can accept
            pStory.Add("HTTP_ACCEPT_ENCODING", System.Environment.GetEnvironmentVariable("HTTP_ACCEPT_ENCODING"));
            //A list of the HTTP_ACCEPT_ENCODING types that the client can accept
            pStory.Add("HTTP_ACCEPT_CHARSET", System.Environment.GetEnvironmentVariable("HTTP_ACCEPT_CHARSET"));
            //A list of the languages that the client can accept
            pStory.Add("HTTP_ACCEPT_LANGUAGE", System.Environment.GetEnvironmentVariable("HTTP_ACCEPT_LANGUAGE"));
            //The MIME type of the query data, such as \"text/html\"
            pStory.Add("CONTENT_TYPE", System.Environment.GetEnvironmentVariable("CONTENT_TYPE"));
            //The encoding type of the query data, such as \"text/html\"
            pStory.Add("CONTENT_ENCODING", System.Environment.GetEnvironmentVariable("CONTENT_ENCODING"));

            //////

            int LengthCompare = PostLength;

            if (PostLength > 0) PostThread.Start();

            logError("INFO", "postedStoryParse: Begin streaming...");

            while (PostLength > 0)
            {
                Thread.Sleep(max_con_timeout);
                logError("INFO", "postedStoryParse: Streaming... " + PostLength +" ... " + LengthCompare);
                if (PostLength < LengthCompare)
                    LengthCompare = PostLength;
                else
                {
                    logError("INFO", "postedStoryParse: Streaming data or connection problem. PostLength " + PostLength + ", LengthCompare " + LengthCompare);
                    break;
                }
            }

            logError("INFO", "postedStoryParse: End streaming... begin adding to dictionary... ");

            string PostData = new string(chararray);

            pStory.Add("raw_story", PostData);

            logError("INFO", "postedStoryParse: End adding to dictionary... ");

            PostData = Sanitize(PostData);

            logError("INFO", "postedStoryParse: Begin splitting &... ");

            string[] postDataSplit = PostData.Split('&');

            logError("INFO", "postedStoryParse: End splitting &... ");

            foreach (string param in postDataSplit)
            {
                logError("INFO", "postedStoryParse: Begin splitting...");

                string[] kvPair = param.Split('=');
                string key = kvPair[0];

                logError("INFO", "postedStoryParse: Begin decoding for " + key + "...");

                string value = HttpUtility.UrlDecode(kvPair[1]);
                //string value = WebUtility.UrlDecode(kvPair[1]);
                //string value = WebUtility.HtmlDecode(kvPair[1]);
                logError("INFO", "postedStoryParse: Adding... " + key+ " ... " +value);
                pStory.Add(key, value);
            }

            logError("INFO", "postedStoryParse: End reading...");

            return pStory;
        }

        private static string getHtmlHead(string htmlStylesheet)
        {
            string head = "";

            string htmlTitle = ":: fetlit :: express yourself :: post your story ::";
            //string htmlStylesheet = "preview.css";
            //string htmlStylesheet = "style.css";

            head += "<!DOCTYPE html><html>";
            head += "<head>";
            head += String.Format("<title>{0}</title>", htmlTitle);
            head += String.Format("<link href=\"{0}\" media=\"all\" rel=\"stylesheet\" type=\"text/css\" />", htmlStylesheet);
            head += "</head>";
            head += "<body>";
            head += "<div class=\"tiles clearfix\">";
            head += "<div class=\"w4 h3\">";
            //head += "<form name=\"post_story_banner\" action=\""+htmlAction+"\" method=\"post\">";
            //head += "<input name=\"post_story_button\" type=\"hidden\" value=\"post_story\">";
            //head += "<span onclick=\"post_story_banner.submit()\">";

            head += "<form name=\"search_story_banner\" action=\"" + htmlAction + "\" method=\"post\">";
            head += "<input name=\"search_story_button\" type=\"hidden\" value=\"search_story\">";
            head += "<span onclick=\"search_story_banner.submit()\">";

            head += "<a href=\"http://"+htmlRoot+"\"><span></span></a>";
            head += String.Format("<h1>{0}</h1>", htmlTitle);
            head += "</span>";
            head += "</form>";
            head += "</div>";
            head += "</div>";

            return head;
        }

        private static string renderStory(string story)
        {
            string rendered_story = "";

            rendered_story += story.Replace("    ", " ");
            rendered_story = rendered_story.Replace("   ", " ");
            rendered_story = rendered_story.Replace("  ", " ");
            rendered_story = rendered_story.Replace("  ", " ");

            rendered_story = rendered_story.Replace("\n\n", "&&");
            rendered_story = rendered_story.Replace("\n", " ");

            rendered_story = rendered_story.Replace("&&&&", "&&");
            rendered_story = rendered_story.Replace("&&&&", "&&");
            rendered_story = rendered_story.Replace("&&&&", "&&");

            rendered_story = rendered_story.Replace("&&", "</h3><h3>");

            return "<h3>"+rendered_story+"</h3>";
        }

        private static bool postStory()
        {

            Dictionary<string, string> dictCategories = getCategories();

            Dictionary<string, string> dictLanguages = getLanguages();

            //postedStory = Cgi.postedStoryParse();

            string htmlStylesheet = "preview.css";

            string htmlStoryTitle = "On the extremes of good and evil";
            string htmlStoryAuthorName = "Marcus Tulii Ciceronis";
            string htmlStoryAuthorEmail = "ciceronis@fetlit.com";
            string htmlStoryAuthorSecret = "6789";
            string htmlStorySecret = "9876543212345670";
            string htmlStoryTagline = "The beginnings of all things are small.";
            string htmlStorySummary = "I must explain to you how all this mistaken idea of denouncing pleasure and praising pain was born and I will give you a complete account of the system, and expound the actual teachings of the great explorer of the truth, the master-builder of human happiness. No one rejects, dislikes, or avoids pleasure itself, because it is pleasure, but because those who do not know how to pursue pleasure rationally encounter consequences that are extremely painful. Nor again is there anyone who loves or pursues or desires to obtain pain of itself, because it is pain, but because occasionally circumstances occur in which toil and pain can procure him some great pleasure. To take a trivial example, which of us ever undertakes laborious physical exercise, except to obtain some advantage from it? But who has any right to find fault with a man who chooses to enjoy a pleasure that has no annoying consequences, or one who avoids a pain that produces no resultant pleasure?";
            string htmlStoryCleanBody =
                 "Chapter One\n\n"
                + "On Love\n\n"
                + "Every living creature loves itself, and from the moment of birth strives to secure its own preservation; because the earliest impulse bestowed on it by nature for its life-long protection is the instinct for self-preservation and for the maintenance of itself in the best condition possible to it in accordance with its nature. At the outset this tendency is vague and uncertain, so that it merely aims at protecting itself whatever its character may be; it does not understand itself nor its own capacities and nature. When, however, it has grown a little older, and has begun to understand the degree in which different things affect and concern itself, it now gradually commences to make progress. Self-consciousness dawns, and the creature begins to comprehend the reason why it possesses the instinctive appetition aforesaid, and to try to obtain the things which it perceives to be adapted to its nature and to repel their opposites.\n\n"
                + "Every living creature therefore finds its object of appetition in the thing suited to its nature. Thus arises The End of Goods, namely to live in accordance with nature and in that condition which is the best and most suited to nature that is possible. At the same time every animal has its own nature; and consequently, while for all alike the End consists in the realization of their nature (for there is no reason why certain things should not be common to all the lower animals, and also to the lower animals and man, since all have a common nature), yet the ultimate and supreme objects that we are investigating must be differentiated and distributed among the different kinds of animals, each kind having its own peculiar to itself and adapted to the requirements of its individual nature.\n\n"
                + "Hence when we say that the End of all living creatures is to live in accordance with nature, this must not be construed as meaning that all have one and the same end; but just as it is correct to say that all the arts and sciences have the common characteristic of occupying themselves with some branch of knowledge, while each art has its own particular branch of knowledge belonging to it, so all animals have the common End of living according to nature, but their natures are diverse, so that one thing is in accordance with nature for the horse, another for the ox, and another for man, and yet in all the Supreme End is common, and that not only in animals but also in all those things upon which nature bestows nourishment, increase and protection. Among these things we notice that plants can, in a sense, perform on their own behalf a number of actions conducive to their life and growth, so that they may attain their End after their kind. So that finally we may embrace all animate existence in one broad generalization, and say without hesitation, that all nature is self-preserving, and has before it the end and aim of maintaining itself in the best possible condition after its kind; and that consequently all things endowed by nature with life have a similar, but not an identical, End. This leads to the inference, that the ultimate Good of man is life in accordance with nature, which we may interpret as meaning life in accordance with human nature developed to its full perfection and supplied with all its needs.";
            string htmlStoryCategory = "category";
            string htmlStoryLanguage = "English";

            string htmlStoryKeywords = "Virgin, werewolf, teen, blood";
            string htmlAgreeTOC = "";

            string htmlErrorTOC = "";


            Console.Write(getHtmlHead(htmlStylesheet));

            Console.Write("<br />");

            Console.Write("<div class=\"submit_story\">");
            Console.Write("<form action=\"{0}\" method=\"POST\">", htmlAction);
            Console.Write("<h2>");

            Console.Write("Author pen name (required, 42 characters max): <br/>");
            Console.Write("<INPUT required type=\"text\" NAME=\"author\" SIZE=\"100\" minlength=\"5\" maxlength=\"42\" placeholder=\"{0}\"/><br/><br/>", htmlStoryAuthorName);
            Console.Write("Author email (100 characters max, to receive your secrets, not required): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"email\" SIZE=\"100\" maxlength=\"100\" placeholder=\"{0}\"/><br/><br/>", htmlStoryAuthorEmail);
            Console.Write("Author secret (if any, 4 digits, to use your existing name, not required for new authors): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"asecret\" size=\"100\" maxlength=\"4\"  placeholder=\"{0}\"/><br/><br/>", htmlStoryAuthorSecret);
            Console.Write("Story secret (if any, 16 digits, to update/overwrite/delete your existing story, not required for new stories): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"ssecret\"  size=\"100\" maxlength=\"16\" placeholder=\"{0}\"/><br/><br/>", htmlStorySecret);
            Console.Write("Story title (required, 60 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"title\" SIZE=\"100\" minlength=\"5\" maxlength=\"60\" placeholder=\"{0}\"/><br/><br/>", htmlStoryTitle);
            Console.Write("Story tagline (required, 90 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"tagline\" SIZE=\"100\" minlength=\"5\" maxlength=\"90\" placeholder=\"{0}\"/><br/><br/>", htmlStoryTagline);
            Console.Write("Story keywords (required, comma separated, 90 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"keywords\" SIZE=\"100\" minlength=\"5\" maxlength=\"90\" placeholder=\"{0}\"/><br/><br/>", htmlStoryKeywords);

            Console.Write("Story category: <select name=\"category\" id=\"category\">");
            foreach (KeyValuePair<string, string> story_category in dictCategories)
            {
                Console.Write(("<option value=\"" + story_category.Key + "\">" + story_category.Value + "</option>").Replace(htmlStoryCategory + "\">", htmlStoryCategory + "\" selected=\"selected\">"));
            }
            Console.Write("</select><br /><br/>");

            Console.Write("");

            Console.Write("Story language: <select name=\"language\" id=\"language\">");
            foreach (KeyValuePair<string, string> story_language in dictLanguages)
            {
                Console.Write(("<option value=\"" + story_language.Key + "\">" + story_language.Value + "</option>").Replace(htmlStoryLanguage + "\">", htmlStoryLanguage + "\" selected=\"selected\">"));
            }
            Console.Write("</select><br/><br/>");

            Console.Write("Story summary (1000 characters at most, or about 200 words): <br/>");
            Console.Write("<textarea name=\"summary\" id=\"summary\" cols=\"100\" rows=\"10\" maxlength=\"1000\" placeholder=\"{0}\"></textarea><br/><br/>", htmlStorySummary);

            Console.Write("Story text (at least about 10,000 words and at most about 200,000 words): <br/>");
            Console.Write("<textarea name=\"content\" id=\"content\" cols=\"100\" rows=\"30\" maxlength=\"5000000\" placeholder=\"{0}\"></textarea><br/><br/>", htmlStoryCleanBody);

            Console.Write("By posting Your Content on this public Platform,");
            Console.Write("You are granting fetlit a limited, royalty-free,<br/>");
            Console.Write("non-exclusive rights to publish Your Content.");
            Console.Write("You retain all copyrights to Your Content,<br/>");
            Console.Write("and may retract Your Content at any time.");
            Console.Write("To submit Your Content to this Platform,<br/>");
            Console.Write("by checking the box below, You agree to our Terms of Use and also agree that:<br/>");
            Console.Write("1) You own the copyright to the submitted Content,<br/>");
            Console.Write("2) You are at least 18 years old at the time of submission,<br/>");
            Console.Write("3) it is legal to view adult Content in Your jurisdiction, and<br/>");
            Console.Write("4) Your Content does not involve minors, necrophilia, scat and snuff.<br/><br/>");

            Console.Write("I agree to the above:");
            Console.Write("<input type=\"checkbox\" name=\"submit_agree_checkbox\" value=\"1\" {0}><br/><br/>", htmlAgreeTOC);

            Console.Write("<button type=\"submit\" name=\"preview_story_button\" value=\"preview_story\">Preview Your Story</button><br/><br/>");

            Console.Write("For detailed information and guidelines on story submission, please read out FAQ.<br/><br/>");

            Console.Write("</form>");
            Console.Write("</div>");
            Console.Write("</body></html>");

            return true;
        }

        private static bool searchStory()
        {

            Dictionary<string, string> dictCategories = getCategories();

            Dictionary<string, string> dictLanguages = getLanguages();

            string htmlStylesheet = "preview.css";

            Console.Write(getHtmlHead(htmlStylesheet));

            Console.Write("<br />");

            Console.Write("<div class=\"filter_story\">");
            Console.Write("<form action=\"{0}\" method=\"POST\">", htmlAction);
            Console.Write("<h2>");

            Console.Write("Author pen name, wildcards allowed ( *mar*sad* ) : <br/><INPUT type=\"text\" NAME=\"author\" SIZE=\"100\" maxlength=\"42\" /><br/><br/>");

            Console.Write("Story title, wildcards allowed ( *sodom* ) : <br/><INPUT TYPE=\"text\" NAME=\"title\" SIZE=\"100\" maxlength=\"60\" /><br/><br/>");

            Console.Write("Story tagline : <br/><INPUT TYPE=\"text\" NAME=\"tagline\" SIZE=\"100\" maxlength=\"90\"/><br/><br/>");

            Console.Write("Story keywords, comma separated ( xxx, holy, panties ) : <br/><INPUT TYPE=\"text\" NAME=\"keywords\" SIZE=\"100\" maxlength=\"90\"/><br/><br/>");

            Console.Write("Story free text search, exact match, no wildcards : <br/><INPUT TYPE=\"text\" NAME=\"storybody\" SIZE=\"100\" maxlength=\"90\"/><br/><br/>");

            Console.Write("Story from date :&nbsp;&nbsp;<INPUT TYPE=\"date\" NAME=\"fromdate\"/> mm/dd/yyyy<br/><br/>");

            Console.Write("Story to date :&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;<INPUT TYPE=\"date\" NAME=\"todate\"/> mm/dd/yyyy<br/><br/>");

            Console.Write("Story category :&nbsp;&nbsp;&nbsp;<select name=\"category\" id=\"category\">");
            Console.Write("<option value=\"ANY\" selected=\"selected\">Any Category</option>");
            foreach (KeyValuePair<string, string> story_category in dictCategories)
            {
                Console.Write(("<option value=\"" + story_category.Key + "\">" + story_category.Value + "</option>"));
            }
            Console.Write("</select><br /><br/>");

            Console.Write("");

            Console.Write("Story language :&nbsp;&nbsp;<select name=\"language\" id=\"language\">");
            Console.Write("<option value=\"ANY\" selected=\"selected\">Any Language</option>");
            foreach (KeyValuePair<string, string> story_language in dictLanguages)
            {
                Console.Write(("<option value=\"" + story_language.Key + "\">" + story_language.Value + "</option>"));
            }
            Console.Write("</select><br/><br/>");

            Console.Write("Rated at least :&nbsp;&nbsp;&nbsp;&nbsp;" +
            "<select name=\"search_rating\" id=\"search_rating\">" +
            "  <option value=\"1\">1 Star</option>" +
            "  <option value=\"2\">2 Stars</option>" +
            "  <option value=\"3\">3 Stars</option>" +
            "  <option value=\"4\">4 Stars</option>" +
            "  <option value=\"5\">5 Stars</option>" +
            "</select><br /><br />");

            Console.Write("<button type=\"submit\" name=\"filter_story_button\" value=\"filter_story\">Search Stories</button><br/><br/>");

            Console.Write("</h2>");
            Console.Write("</form>");
            Console.Write("</div>");
            Console.Write("</body></html>");

            return true;
        }

        private static bool previewStory(Dictionary<string, string> postedStory)
        {

            //Dictionary<string, string> postedStory = new Dictionary<string, string>();

            Dictionary<string,string> dictCategories = getCategories();

            Dictionary<string,string> dictLanguages = getLanguages();

            //postedStory = Cgi.postedStoryParse();

            string htmlHeader = ":: fetlit :: express yourself ::";
            string htmlStylesheet = "preview.css";
            string htmlStoryTitle = postedStory["title"];
            string htmlStoryAuthorName = postedStory["author"];
            string htmlStoryAuthorEmail = postedStory["email"];
            string htmlStoryAuthorSecret = postedStory["asecret"];
            string htmlStorySecret = postedStory["ssecret"];
            string htmlStoryTagline = postedStory["tagline"];
            string htmlStorySummary = postedStory["summary"];
            string htmlStoryCleanBody = postedStory["content"];
            string htmlStoryPostBody = postedStory["raw_story"];
            string htmlStoryCategory = postedStory["category"];
            string htmlStoryLanguage = postedStory["language"];
            string htmlStoryKeywords = postedStory["keywords"];
            string htmlAgreeTOC = "";
            string htmlSubmitAction = "preview";
            //string htmlAction = "articleread.exe";
            string htmlErrorTOC = "";
            //string html = "";

            logError("INFO", "previewStory: Begin story render...");
            string htmlStoryRenderedBody = renderStory( htmlStoryCleanBody );
            logError("INFO", "previewStory: End story render...");

            bool checkTOC = false;

            try
            {
                if (postedStory["submit_agree_checkbox"].CompareTo("1") == 0)
                {
                    htmlAgreeTOC += "checked";
                    checkTOC = true;
                }
            }
            catch
            (Exception ex)
            {
                htmlErrorTOC += "important :: You must agree to the TOC to post Your Content";
            }

            logError("INFO", "previewStory: Begin HTML render...");

            Console.Write(getHtmlHead(htmlStylesheet));


            if (!checkTOC)
            {
                Console.Write("<div class=\"tiles clearfix\">");
                Console.Write("<div class=\"w4 h3\">");
                Console.Write("<a href=\"#preview_story_button\"><span></span></a>");
                Console.Write("<h1 id=\"story_beginning\">:: {0} ::</h1>", htmlErrorTOC);
                Console.Write("</div>");
                Console.Write("</div>");
                Console.Write("&nbsp;");
            }
            
            Console.Write("<table>");
            Console.Write("<tbody>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Title:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2>", htmlStoryTitle);
            Console.Write("</td>");
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Author:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStoryAuthorName);
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Tagline:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStoryTagline);
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Summary:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStorySummary);
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Keywords:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStoryKeywords);
            Console.Write("</tr>");

            Console.Write("</tbody>");
            Console.Write("</table>");

            Console.Write("<table>");
            Console.Write("<tbody>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<p>{0}</p>", htmlStoryRenderedBody);
            Console.Write("</td>");
            Console.Write("</tr>");
            Console.Write("</tbody>");
            Console.Write("</table>");
            Console.Write("<br />");

            Console.Write("<div class=\"submit_story\">");
            Console.Write("<form action=\"{0}\" method=\"POST\">", htmlAction);
            Console.Write("<h2>");
            if (checkTOC) Console.Write("If you are satifsied with the story formatting, submit it by clicking the button below, or edit and preview again.");
            Console.Write("<br/><br/>");
            if (checkTOC) Console.Write("<button type=\"submit\" name=\"submit_story_button\" value=\"submit_story\">Submit Your Story</button><br/><br/>");

            Console.Write("Author pen name (required, 42 characters max): <br/>");
            Console.Write("<INPUT required type=\"text\" NAME=\"author\" SIZE=\"100\" minlength=\"5\" maxlength=\"42\" value=\"{0}\"/><br/><br/>", htmlStoryAuthorName);
            Console.Write("Author email (90 characters max, to receive your secrets, not required): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"email\" SIZE=\"100\" maxlength=\"90\" value=\"{0}\"/><br/><br/>", htmlStoryAuthorEmail);
            Console.Write("Author secret (if any, 4 digits, to use your existing name, not required for new authors): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"asecret\" size=\"100\" maxlength=\"4\"  value=\"{0}\"/><br/><br/>", htmlStoryAuthorSecret);
            Console.Write("Story secret (if any, 16 digits, to update/overwrite/delete your existing story, not required for new stories): <br/>");
            Console.Write("<INPUT TYPE=\"text\" NAME=\"ssecret\"  size=\"100\" maxlength=\"16\" value=\"{0}\"/><br/><br/>", htmlStorySecret);
            Console.Write("Story title (required, 60 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"title\" SIZE=\"100\" minlength=\"5\" maxlength=\"60\" value=\"{0}\"/><br/><br/>", htmlStoryTitle);
            Console.Write("Story tagline (required, 100 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"tagline\" SIZE=\"100\" minlength=\"5\" maxlength=\"100\" value=\"{0}\"/><br/><br/>", htmlStoryTagline);
            Console.Write("Story keywords (required, comma separated, 90 characters max): <br/>");
            Console.Write("<INPUT required TYPE=\"text\" NAME=\"keywords\" SIZE=\"100\" minlength=\"5\" maxlength=\"90\" value=\"{0}\"/><br/><br/>", htmlStoryKeywords);

            Console.Write("Story category: <select name=\"category\" id=\"category\">");
            foreach (KeyValuePair<string, string> story_category in dictCategories)
            {
                Console.Write(("<option value=\"" + story_category.Key + "\">" + story_category.Value + "</option>").Replace(htmlStoryCategory + "\">", htmlStoryCategory + "\" selected=\"selected\">"));
            }
            Console.Write("</select><br /><br/>");

            Console.Write("");

            Console.Write("Story language: <select name=\"language\" id=\"language\">");
            foreach (KeyValuePair<string, string> story_language in dictLanguages)
            {
                Console.Write(("<option value=\"" + story_language.Key + "\">" + story_language.Value + "</option>").Replace(htmlStoryLanguage + "\">", htmlStoryLanguage + "\" selected=\"selected\">"));
            }
            Console.Write("</select><br/><br/>");

            Console.Write("Story summary (1000 characters at most, or about 200 words): <br/>");
            Console.Write("<textarea name=\"summary\" id=\"summary\" cols=\"100\" rows=\"10\" maxlength=\"1000\">{0}</textarea><br/><br/>", htmlStorySummary);

            Console.Write("Story text (at least about 10,000 words and at most about 200,000 words): <br/>");
            Console.Write("<textarea name=\"content\" id=\"content\" cols=\"100\" rows=\"30\" maxlength=\"5000000\">{0}</textarea><br/><br/>", htmlStoryCleanBody); // htmlStoryPostBody); //htmlStoryCleanBody);// htmlStoryRenderedBody);

            Console.Write("By posting Your Content on this public Platform,");
            Console.Write("You are granting fetlit a limited, royalty-free,<br/>");
            Console.Write("non-exclusive rights to publish Your Content.");
            Console.Write("You retain all copyrights to Your Content,<br/>");
            Console.Write("and may retract Your Content at any time.");
            Console.Write("To submit Your Content to this Platform,<br/>");
            Console.Write("by checking the box below, You agree to our Terms of Use and also agree that:<br/>");
            Console.Write("1) You own the copyright to the submitted Content,<br/>");
            Console.Write("2) You are at least 18 years old at the time of submission,<br/>");
            Console.Write("3) it is legal to view adult Content in Your jurisdiction, and<br/>");
            Console.Write("4) Your Content does not involve minors, necrophilia, scat and snuff.<br/><br/>");

            Console.Write("I agree to the above:");
            Console.Write("<input type=\"checkbox\" name=\"submit_agree_checkbox\" value=\"1\" {0}><br/><br/>", htmlAgreeTOC);

            Console.Write("<button id=\"preview_story_button\" type=\"submit\" name=\"preview_story_button\" value=\"preview_story\">Preview Your Story</button><br/><br/>");

            Console.Write("For detailed information and guidelines on story submission, please read out FAQ.<br/><br/>");

            Console.Write("</form>");
            Console.Write("</div>");
            Console.Write("</body></html>");

            logError("INFO", "previewStory: End HTML render...");

            return true;
        }

        private static bool readStory(string story_id, string fingerprint_hash)
        {

            string htmlStylesheet = "preview.css";

            string htmlStoryTitle = "";
            string htmlStoryAuthorName = "";
            string htmlStoryTagline = "";
            string htmlStorySummary = "";
            string htmlStoryRating = "";
            string htmlStoryCleanBody = "";

            Dictionary<string, string> pStory = new Dictionary<string, string>();

            logError("INFO", "readStory: Begin story db read...");

            using (OracleConnection oracleConnection = new OracleConnection())
            {
                try
                {
                    oracleConnection.ConnectionString = Cgi.connectionString;

                    string select_story_sql = "select * from stories where story_id = " + story_id;

                    oracleConnection.Open();
                    OracleCommand command = oracleConnection.CreateCommand();
                    command.CommandText = select_story_sql;
                    OracleDataReader oracleDataReader = command.ExecuteReader();

                    while (oracleDataReader.Read())
                    {

                        Oracle.DataAccess.Types.OracleClob story_clob = oracleDataReader.GetOracleClob(oracleDataReader.GetOrdinal("story_clob"));

                        //StreamReader streamreader = new StreamReader(story_clob, Encoding.Unicode);

                        htmlStoryTitle = Convert.ToString(oracleDataReader["story_name"]);
                        htmlStoryAuthorName = Convert.ToString(oracleDataReader["user_alias"]);
                        htmlStoryTagline = Convert.ToString(oracleDataReader["story_line"]);
                        htmlStorySummary = Convert.ToString(oracleDataReader["story_summary"]);
                        htmlStoryRating = Convert.ToString(oracleDataReader["story_rating_overall"]);
                        htmlStoryCleanBody = (string)story_clob.Value;
                    }
                    oracleDataReader.Close();
                    command.Dispose();
                }
                catch (Exception ex)
                {
                    Cgi.logError(((object)ex).ToString());
                    throw new Exception(((object)ex).ToString());
                }
                finally
                {
                    oracleConnection.Close();
                    oracleConnection.Dispose();
                }
            }

            logError("INFO", "readStory: End story db read...");

            incStoryReadCounter(story_id);

            logError("INFO", "readStory: Begin story render...");

            string htmlStoryRenderedBody = renderStory(htmlStoryCleanBody);

            logError("INFO", "readStory: End story render...");

            htmlStoryRating = getStarRating(Convert.ToDouble(htmlStoryRating));

            Dictionary<string,string> dictCategories = getCategories();

            Dictionary<string,string> dictLanguages = getLanguages();

            logError("INFO", "readStory: Begin HTML render...");

            Console.Write(getHtmlHead(htmlStylesheet));

            Console.Write("<table>");
            Console.Write("<tbody>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Title:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2>", htmlStoryTitle);
            Console.Write("</td>");
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Author:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStoryAuthorName);
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Tagline:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStoryTagline);
            Console.Write("</tr>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("<h2>Summary:</h2></td>");
            Console.Write("<td>");
            Console.Write("<h2>{0}</h2></td>", htmlStorySummary);
            Console.Write("</tr>");
            Console.Write("</tbody>");
            Console.Write("</table>");
            Console.Write("&nbsp;");

            if (getFingerprintCheck(fingerprint_hash, story_id) > 0)
            {
                Console.Write("<div class=\"tiles clearfix\">");
                Console.Write("<div class=\"w5 h5\">");
                Console.Write("<a href=\"#comments\"><span></span></a>");
                Console.Write("<h1 id=\"story_beginning\">:: " + htmlStoryRating + " :: go to all comments ::</h1>");
                Console.Write("</div>");
                Console.Write("</div>");
                Console.Write("&nbsp;");
            }
            else
            {
                Console.Write("<div class=\"rating\"><table><tbody><tr><td>");
                Console.Write("<form class=\"rating\" name=\"rate_comment_story\" action=\""+htmlAction+"\" method=\"POST\">");
                Console.Write("<h2>Please rate and/or comment on the story; clicking on the rating will also submit your comments (about 1000 words at most):</h2>");
                Console.Write("<textarea name=\"comment\" id=\"comment\" cols=\"85\" rows=\"13\" maxlength=\"3900\"></textarea><br/><br/>");
                Console.Write("<input type=\"submit\" type=\"radio\" id=\"star5\" name=\"rating\" value=\"5\" /><label for=\"star5\" title=\"Best!\">5 stars</label>");
                Console.Write("<input type=\"submit\" type=\"radio\" id=\"star4\" name=\"rating\" value=\"4\" /><label for=\"star4\" title=\"Good.\">4 stars</label>");
                Console.Write("<input type=\"submit\" type=\"radio\" id=\"star3\" name=\"rating\" value=\"3\" /><label for=\"star3\" title=\"So so.\">3 stars</label>");
                Console.Write("<input type=\"submit\" type=\"radio\" id=\"star2\" name=\"rating\" value=\"2\" /><label for=\"star2\" title=\"Bad.\">2 stars</label>");
                Console.Write("<input type=\"submit\" type=\"radio\" id=\"star1\" name=\"rating\" value=\"1\" /><label for=\"star1\" title=\"Sucks!\">1 star</label>");
                Console.Write("<input type=\"hidden\" name=\"story_id\" value=\"" + story_id + "\"/>");
                Console.Write("</form></td></tr></tbody></table></div>");
                Console.Write("&nbsp;");
                Console.Write("<div class=\"tiles clearfix\">");
                Console.Write("<div class=\"w5 h5\">");
                Console.Write("<a href=\"#comments\"><span></span></a>");
                Console.Write("<h1 id=\"story_beginning\">:: top of the story :: go to all comments ::</h1>");
                Console.Write("</div>");
                Console.Write("</div>");
                Console.Write("&nbsp;");
            }

            Console.Write("<table>");
            Console.Write("<tbody>");
            Console.Write("<tr>");
            Console.Write("<td>");
            Console.Write("{0}", htmlStoryRenderedBody);
            Console.Write("</td>");
            Console.Write("</tr>");
            Console.Write("</tbody>");
            Console.Write("</table>");
            Console.Write("&nbsp;");

            Console.Write("<div class=\"tiles clearfix\">");
            Console.Write("<div class=\"w5 h5\">");
            Console.Write("<a href=\"#story_beginning\"><span></span></a>");
            Console.Write("<h1 id=\"comments\">:: comments :: go to the top of the story ::</h1>");
            Console.Write("</div>");
            Console.Write("</div>");
            Console.Write("&nbsp;");

            // comments section at the end
            Console.Write("<table>");
            Console.Write("<tbody>");
            Console.Write(getStoryComments(story_id));            
            Console.Write("</tbody>");
            Console.Write("</table>");
            Console.Write("&nbsp;");

            Console.Write("<div class=\"tiles clearfix\">");
            Console.Write("<div class=\"w5 h5\">");
            Console.Write("<a href=\"#story_beginning\"><span></span></a>");
            Console.Write("<h1 id=\"comments\">:: the end :: go to the top of the story ::</h1>");
            Console.Write("</div>");
            Console.Write("</div>");
            Console.Write("&nbsp;");

            Console.Write("</body></html>");

            logError("INFO", "readStory: End HTML render...");

            return true;
        }

        private static bool postStory(Dictionary<string, string> postedStory)
        {

            try
            {
                if (postedStory["submit_agree_checkbox"].CompareTo("1") == 0)
                {

                    string htmlStoryAuthorName = postedStory["author"];
                    string htmlStoryAuthorSecret = postedStory["asecret"];
                    string htmlStorySecret = postedStory["ssecret"];
                    string htmlStoryTitle = postedStory["title"];

                    string stored_or_new_author_secret = getUserSecret(htmlStoryAuthorName, htmlStoryAuthorSecret);
                    string stored_or_new_story_secret = getStorySecret(htmlStoryAuthorName, htmlStoryTitle, htmlStorySecret);
                    string story_id = getStoryID(htmlStoryAuthorName, htmlStoryTitle, htmlStorySecret);
                    postedStory.Add("story_id", story_id);

                    // existing author, posted secret, no matching author secret found, bail
                    if (stored_or_new_author_secret.CompareTo("wrongauthorsecret") == 0)
                    {
                        Console.Write(getHtmlHead("style.css"));
                        Console.Write("<div class=\"tiles clearfix\">");
                        Console.Write("<div class=\"w4 h3\">");
                        Console.Write("<a><span></span></a>");
                        Console.Write("<h1 id=\"story_beginning\">:: author or secret not found or author secret incorrect ::</h1>");
                        Console.Write("</div>");
                        Console.Write("</div>");
                        Console.Write("&nbsp;");
                        Console.Write("</body></html>");

                    }
                    else if (stored_or_new_story_secret.CompareTo("wrongstorysecret") == 0)
                    {
                        Console.Write(getHtmlHead("style.css"));
                        Console.Write("<div class=\"tiles clearfix\">");
                        Console.Write("<div class=\"w4 h3\">");
                        Console.Write("<a><span></span></a>");
                        Console.Write("<h1 id=\"story_beginning\">:: story or secret not found or story secret incorrect ::</h1>");
                        Console.Write("</div>");
                        Console.Write("</div>");
                        Console.Write("&nbsp;");
                        Console.Write("</body></html>");
                    }
                    // existing author, posted secret, matching author secret found, insert with old secret
                    else if (stored_or_new_author_secret.CompareTo(postedStory["asecret"]) == 0)
                    {

                        if (stored_or_new_story_secret.CompareTo(postedStory["ssecret"]) == 0)
                        {
                            updateStory(postedStory);

                            Console.Write(getHtmlHead("style.css"));
                            Console.Write("<div class=\"tiles clearfix\">");
                            Console.Write("<div class=\"w4 h3\">");
                            Console.Write("<a><span></span></a>");
                            Console.Write("<h1 id=\"story_beginning\">:: thanks for updating ::</h1>");
                            Console.Write("</div>");
                            Console.Write("</div>");
                            Console.Write("&nbsp;");
                            Console.Write("</body></html>");
                        }
                        else
                        {
                            postedStory.Remove("ssecret");
                            postedStory.Add("ssecret", stored_or_new_story_secret);

                            insertStory(postedStory);

                            Console.Write(getHtmlHead("style.css"));
                            Console.Write("<div class=\"tiles clearfix\">");
                            Console.Write("<div class=\"w4 h3\">");
                            Console.Write("<a><span></span></a>");
                            Console.Write("<h1 id=\"story_beginning\">:: thanks for posting :: " + htmlStoryTitle + " :: story secret " + stored_or_new_story_secret + " ::</h1>");
                            Console.Write("</div>");
                            Console.Write("</div>");
                            Console.Write("&nbsp;");
                            Console.Write("</body></html>");

                        }

                    }
                    // new author, new story, empty or some posted secrets, no matching author and story secret found, insert with new secrets
                    else
                    {
                        postedStory.Remove("asecret");
                        postedStory.Add("asecret", stored_or_new_author_secret);
                        postedStory.Remove("ssecret");
                        postedStory.Add("ssecret", stored_or_new_story_secret);

                        insertStory(postedStory);

                        Console.Write(getHtmlHead("style.css"));
                        Console.Write("<div class=\"tiles clearfix\">");
                        Console.Write("<div class=\"w4 h3\">");
                        Console.Write("<a><span></span></a>");
                        Console.Write("<h1 id=\"story_beginning\">:: thanks for posting :: " + htmlStoryAuthorName + " :: author secret " + stored_or_new_author_secret + " :: " + htmlStoryTitle + " :: story secret " + stored_or_new_story_secret + "</h1>");
                        Console.Write("</div>");
                        Console.Write("</div>");
                        Console.Write("&nbsp;");
                        Console.Write("</body></html>");

                    }

                }
            }
            catch
            (Exception ex)
            {
                previewStory(postedStory);
            }

            return true;
        }
        
        [STAThread]
        static void Main(string[] args)
        {
            logError("INFO", "Main: Begin job...");

            SetConsoleMode(3, 0);
            Console.Write("Content-Type: text/html\n\n"); // CGI compliant string

            string postAction = "";

            EventLog el = new EventLog();

            Cgi.Go(el);

            Dictionary<string, string> postedStory = new Dictionary<string, string>();

            logError("INFO", "Main: Parsing...");
            postedStory = Cgi.postedStoryParse();


            ////////////////
            // calculate the fingerpring of the caller
            string fingerprint_hash =
            sha256(
                        postedStory["REMOTE_ADDR"] +
                        postedStory["HTTP_USER_AGENT"] +
                        postedStory["HTTP_ACCEPT"] +
                        postedStory["HTTP_ACCEPT_ENCODING"] +
                        postedStory["HTTP_ACCEPT_LANGUAGE"] +
                        postedStory["CONTENT_TYPE"]
                        );
            //Console.WriteLine("fingerprint_hash >>>> " + fingerprint_hash);
            ////////////////


            ///////// debug section
            /*
            foreach (KeyValuePair<string, string> story_category in postedStory)
            {
                Console.WriteLine("KEY >>>> " + story_category.Key + " >>> VALUE >>>> " + story_category.Value);
            }
            */
            ///////// end debug section

            try
            {
                postAction = postedStory["main_board_button"];
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["read_story_button"];

                logError("INFO", "Main: Reading...");
                Cgi.readStory(postAction, fingerprint_hash);

            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["post_story_button"];
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["preview_story_button"];
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["search_story_button"];
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["filter_story_button"];
                logError("INFO", "Main: filter_story_button... " + postAction);
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["submit_story_button"];
            }
            catch (Exception e) { ;}

            try
            {
                postAction = postedStory["rating"];
                //logError("INFO", "Main: Rating...");
                rateStory(postedStory, commentStory(postedStory, fingerprint_hash), fingerprint_hash);
                readStory(postedStory["story_id"], fingerprint_hash);
            }
            catch (Exception e) { ;}

            //Console.WriteLine(">>> "+postAction);

            if (postAction.CompareTo("main_board") == 0)
            {
                //Console.WriteLine(">>> getStoryTable...");
                Cgi.getStoryTable();
            }
            else if (postAction.CompareTo("post_story") == 0)
            {
                //Console.WriteLine(">>> previewing story...");
                Cgi.postStory();
            }
            else if (postAction.CompareTo("search_story") == 0)
            {
                //Console.WriteLine(">>> previewing story...");
                Cgi.searchStory();
            }
            else if (postAction.CompareTo("filter_story") == 0)
            {
                //Console.WriteLine(">>> previewing story...");
                logError("INFO", "Main: postAction.CompareTo... " + postAction);
                Cgi.getStorySearch(postedStory);
            }
            else if (postAction.CompareTo("preview_story") == 0)
            {
                //Console.WriteLine(">>> previewing story...");
                logError("INFO", "Main: Previewing...");
                Cgi.previewStory(postedStory);
            }
            else if (postAction.CompareTo("submit_story") == 0)
            {
                //Console.WriteLine(">>> submitting story...");
                logError("INFO", "Main: Submitting...");
                Cgi.postStory(postedStory);
            }
            else
            {
                ;
            }

            
            Environment.Exit(0);

        }  // End of Main().
    }  // End of Cgi class.
} // End of cgi_in_csharp namespace.


/*
select * from (
select 
Q1.position as p1, Q1.string as s1, 
Q2.position as p2, Q2.string as s2
from 
table(string_indexes((SELECT lower(story_clob) FROM STORIES WHERE story_id = 472),lower('little girl'))) Q1,
table(string_indexes((SELECT lower(story_clob) FROM STORIES WHERE story_id = 472),lower('hello'))) Q2
)
where
abs(p1-p2) < 100
*/