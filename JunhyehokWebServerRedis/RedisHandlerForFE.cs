using System;
using System.Linq;
using System.Runtime.InteropServices;
using StackExchange.Redis;
using Junhaehok.CFProtocol;

namespace JunhyehokWebServerRedis
{
    class RedisHandlerForFE
    {
        const string REDIS_IP = "10.100.58.4";
        const int REDIS_PORT = 36379;

        string feName = null;
        Redis redis = null;

        public RedisHandlerForFE(string feAddress)
        {

            redis = new Redis(new ConfigurationOptions
            {
                EndPoints =
                                            {
                                                { REDIS_IP, REDIS_PORT }
                                            },
                KeepAlive = 180,
                Password = "433redis!",
                AllowAdmin = true
            });
            feName = redis.GetFEName(feAddress);
        }
       
        //SIGNOUT
        public bool SignOut(long userid)
        {
            string username = redis.GetUserName(userid);
            redis.DelUser(username);
            redis.DelUserList(username);
            return redis.SetUserLogin(feName, userid, false);
        }

        //ROOM_LIST = 400;
        public byte[] GetRoomList()
        {
            string[] feList = (string[])redis.GetFEList();

            int[] chatRoomList = null;
            foreach (string fe in feList)
            {
                string feName = redis.GetFEName(fe);
                if (chatRoomList != null)
                    chatRoomList = chatRoomList.Concat((int[])redis.GetFERoomList(feName)).ToArray();
                else
                    chatRoomList = (int[])redis.GetFERoomList(feName);
            }

            return  chatRoomList.SelectMany(BitConverter.GetBytes).ToArray();
           
        }

        //CREATE_ROOM = 500;
        public int CreateRoom()
        {
            return redis.CreateRoom(feName);
        }

        //JOIN = 600;
        public bool JoinRoom(long userid, int roomNo)
        {
            string username = redis.GetUserName(userid);
            return redis.AddUserRoom(feName, roomNo, username);
        }

        //LEAVE_ROOM = 700;
        public bool LeaveRoom(long userid, int roomNo)
        {
            string username = redis.GetUserName(userid);
            return redis.LeaveRoom(feName, roomNo, username);
        }


        //DESTROY_ROOM = 800;
        public bool DestroyRoom(int roomNo)
        {
            return redis.DelFERoom(feName, roomNo);
        }

        //MSG = 900;
        public void MSG(long userid)
        {
            string username = redis.GetUserName(userid);
            bool isDummy = redis.GetUserType(username);

            if (!isDummy)
                redis.AddChat(username);  
        }

        //RANKINGS = 1400;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="count">how many do you want to get ranking?</param>
        public byte[] UserRanking(int count)
        {
            SortedSetEntry[] results = (SortedSetEntry[])redis.GetRanking(count);
            UserHandle[] ranking = new UserHandle[results.Length];
            byte[] resultArr = null;
            for (int idx = 0; idx < results.Length; idx++)
            {
                ranking[idx] = new UserHandle();
                ranking[idx].ID = new char[12];

                Array.Copy(((string)results[idx].Element).ToCharArray(), ranking[idx].ID, ((string)results[idx].Element).ToCharArray().Length);

                ranking[idx].Rank = idx + 1;
                ranking[idx].MSGCOUNT = (int)results[idx].Score;
            }

            if (results.Length != 0)
            {
                resultArr = new byte[Marshal.SizeOf(typeof(UserHandle)) * ranking.Length];
                int idx = 0;

                foreach (UserHandle userrank in ranking)
                {
                    int datasize = Marshal.SizeOf(userrank);
                    IntPtr buff = Marshal.AllocHGlobal(datasize);
                    Marshal.StructureToPtr(userrank, buff, false);
                    byte[] data = new byte[datasize];
                    Marshal.Copy(buff, data, 0, datasize);
                    Marshal.FreeHGlobal(buff);
                    Array.Copy(data, 0, resultArr, idx * (datasize), data.Length);

                    idx++;
                }
            }
            return resultArr;
        }

    }
}
