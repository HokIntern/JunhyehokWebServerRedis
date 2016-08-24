using System;
using StackExchange.Redis;
using System.Text;
using System.Threading;
using System.Linq;

namespace JunhyehokWebServerRedis
{
    class Redis
    {
        public static int feNo = 0;
        public IDatabase db;

        private static ConfigurationOptions configurationOptions;

        public Redis(ConfigurationOptions conf)
        {
            configurationOptions = conf;
            db = Connection.GetDatabase();
            if (db.IsConnected("fe:list"))
                Console.WriteLine("[ Redis ][ Connect ] Success");
            else
            {
                Console.WriteLine("[ Redis ][ Connect ] Fail");
            }
        }

        private readonly Lazy<ConnectionMultiplexer> LazyConnection
            = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(configurationOptions));

        private ConnectionMultiplexer Connection
        {
            get
            {
                return LazyConnection.Value;
            }
        }


        private string CombineDelimiter(string s1, string s2, string delimiter)
        {
            StringBuilder result = new StringBuilder();
            result.Append(s1);
            result.Append(delimiter);
            result.Append(s2);

            return result.ToString();
        }

        public string AddFEInfo(string ip, int port)
        {
            string key = CombineDelimiter(ip, port.ToString(), ":");
            string feName = "fe" + GenerateFEName();
            if (db.StringSet(key, feName))
                return feName;
            return "";
        }

        public bool DelFEInfo(string ip, int port)
        {
            string key = CombineDelimiter(ip, port.ToString(), ":");
            return db.KeyDelete(key);
        }

        public void AddFEInfoForClient(string feName, string ip, int port)
        {
            string key = CombineDelimiter(feName, "forclient", ":");

            HashEntry[] entries = new HashEntry[2];
            entries[0] = new HashEntry("ip", ip);
            entries[1] = new HashEntry("port", port);

            db.HashSet(key, entries);
        }

        public bool DelFEInfoForClient(string feName)
        {
            string key = CombineDelimiter(feName, "forclient", ":");
            return db.KeyDelete(key);
        }


        public bool HasRoom(string feName, int roomNo)
        {
            string key = CombineDelimiter(feName, "room", ":");
            return db.SetContains(key, roomNo);
        }

        public bool AddFEList(string ip, int port)
        {
            string key = "fe:list";
            string value = CombineDelimiter(ip, port.ToString(), ":");
            return db.SetAdd(key, value);
        }


        public object GetFEList()
        {
            string key = "fe:list";
            string[] feList = null;

            RedisValue[] result = db.SetMembers(key);

            feList = new string[result.Length];
            for (int idx = 0; idx < result.Length; idx++)
            {
                feList[idx] = result[idx];
            }

            return feList;
        }


        public bool DelFEList(string ip, int port)
        {
            string key = "fe:list";
            string value = CombineDelimiter(ip, port.ToString(), ":");
            return db.SetRemove(key, value);
        }

        public bool AddUser(string username, long userid)
        {
            return db.StringSet(username, userid);
        }

        public long GetUserID(string username)
        {
            return (long)db.StringGet(username);
        }

        public string GetUserName(long userid)
        {
            string[] userList = (string[])GetUserList();
            string username = null;

            foreach (string user in userList)
            {
                if (userid == GetUserID(user))
                {
                    username = user;
                    break;
                }
            }
            return username;
        }

        public bool DelUser(string username)
        {
            return db.KeyDelete(username);
        }

        public bool AddUserList(string username)
        {
            string key = "user:list";
            string value = username;
            return db.SetAdd(key, value);
        }


        public object GetUserList()
        {
            string key = "user:list";
            string[] userList = null;

            RedisValue[] result = db.SetMembers(key);

            userList = new string[result.Length];
            for (int idx = 0; idx < result.Length; idx++)
            {
                userList[idx] = result[idx];
            }

            return userList;
        }

        public bool DelUserList(string username)
        {
            string key = "user:list";
            string value = username;
            return db.SetRemove(key, value);
        }

        public string GetFEName(String feIpPort)
        {
            string key = feIpPort;

            string feName = db.StringGet(key);
            return feName;
        }


        public bool SetUserLogin(string feName, long userID)
        {
            string key = CombineDelimiter(feName, "login", ":");

            return db.SetAdd(key, userID);
        }

        public bool GetUserLogin(string feName, long userID)
        {
            string key = CombineDelimiter(feName, "login", ":");

            RedisValue[] result = db.SetMembers(key);

            for (int idx = 0; idx < result.Length; idx++)
            {
                if (result[idx] == userID)
                {
                    return true;
                }

            }
            return false;
        }

        public bool DelUserLoginFE(string feName)
        {
            string key = CombineDelimiter(feName, "login", ":");
            return db.KeyDelete(key);
        }

        public bool DelUserLogin(string feName, long userID)
        {
            string key = CombineDelimiter(feName, "login", ":");
            bool b1 = db.SetRemove(key, userID);
            return b1;
            //return db.SetRemove(key, userID);
        }

        public bool SetUserType(string id, bool userType)
        {
            long userID = GetUserID(id);

            return db.StringSetBit("user", userID, userType);
        }


        public bool GetUserType(string id)
        {
            long userID = GetUserID(id);

            return db.StringGetBit("user", userID);
        }


        public int CreateRoom(string feName)
        {
            int[] roomlist = GetTotalRoomList();
            Array.Sort(roomlist);

            int roomNo = 1;
            if (roomlist.Length > 0)
                roomNo = roomlist[roomlist.Length - 1] + 1;

            string key = CombineDelimiter(feName, "roomlist", ":");

            db.SetAdd(key, roomNo);
            NewRoomCount(feName, roomNo);

            return roomNo;
        }

        public bool AddUserRoom(string feName, int roomNo, string id)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "user", ":");
            IncRoomCount(feName, roomNo);
            return db.SetAdd(key, id);
        }


        public bool LeaveRoom(string feName, int roomNo, string id)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "user", ":");
            DecRoomCount(feName, roomNo);
            return db.SetRemove(key, id);
        }

        public object GetFERoomList(string feName)
        {
            string key = CombineDelimiter(feName, "roomlist", ":");

            int[] roomList = new int[db.SetLength(key)];

            RedisValue[] values = db.SetMembers(key);

            for (int idx = 0; idx < values.Length; idx++)
            {
                roomList[idx] = (int)values[idx];
            }

            return roomList;
        }

        public int[] GetTotalRoomList()
        {
            string[] feList = (string[])GetFEList();

            int[] roomList = null;
            foreach (string fe in feList)
            {
                string feName = GetFEName(fe);
                if (roomList != null)
                    roomList = roomList.Concat((int[])GetFERoomList(feName)).ToArray();
                else
                    roomList = (int[])GetFERoomList(feName);
            }

            return roomList;
        }


        public bool NewRoomCount(string feName, int roomNo)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "count", ":");
            return db.StringSet(key, 0);
        }

        public int IncRoomCount(string feName, int roomNo)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "count", ":");
            return (int)db.StringIncrement(key, 1);
        }

        public int GetRoomCount(string feName, int roomNo)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "count", ":");
            return (int)db.StringGet(key);
        }

        public int DecRoomCount(string feName, int roomNo)
        {
            string key = CombineDelimiter(CombineDelimiter(feName, "room" + roomNo, ":"), "count", ":");
            return (int)db.StringDecrement(key, 1);
        }

        public bool DelFERoomList(string feName)
        {
            string key = CombineDelimiter(feName, "roomlist", ":");

            return db.KeyDelete(key);
        }

        public bool DelFERoom(string feName, int roomNo)
        {
            string key = CombineDelimiter(feName, "roomlist", ":");

            return db.SetRemove(key, roomNo);
        }


        public void AddChat(string id)
        {

            db.SortedSetIncrement("ranking:chatting", id, 1);

        }

        public object GetRanking(int range)
        {
            SortedSetEntry[] ranks = db.SortedSetRangeByRankWithScores("ranking:chatting", 0, range, Order.Descending);
            return ranks;
        }


        public static int GenerateFEName()
        {
            Interlocked.Increment(ref feNo);
            return feNo;
        }
    }
}