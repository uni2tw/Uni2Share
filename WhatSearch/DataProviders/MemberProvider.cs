using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WhatSearch.DataProviders.Interfaces;
using WhatSearch.Models;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.DataProviders
{
    public class MemberProvider : IMemberProvider
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        static object thisLock = new object();
        public List<Member> GetMembers()
        {
            lock (thisLock)
            {
                try
                {
                    string dataPath = Helper.GetRelativePath("data", "userData", "users.json");
                    if (File.Exists(dataPath))
                    {
                        string jsonStr = File.ReadAllText(dataPath);
                        List<Member> members = JsonHelper.Deserialize<List<Member>>(jsonStr);
                        return members;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex);
                }
                return new List<Member>();
            }
        }

        public Member GetMember(string name)
        {
            return GetMembers().FirstOrDefault(t => t.Name == name);
        }

        public Member GetMemberByToken(string accessToken)
        {
            return GetMembers().FirstOrDefault(t => t.AccessToken == accessToken);
        }

        // 新增會員；已存在的會員整筆更新（改單一欄位請用 Update，避免用過期的資料蓋掉別人剛改的欄位）
        public void SaveMember(Member mem)
        {
            lock (thisLock)
            {
                var members = GetMembers();
                int index = members.FindIndex(t => t.Name == mem.Name);
                if (index < 0)
                {
                    members.Add(mem);
                }
                else
                {
                    members[index] = mem;
                }
                Write(members);
            }
        }

        public bool Update(string name, Action<Member> mutate)
        {
            lock (thisLock)
            {
                var members = GetMembers();
                Member target = members.FirstOrDefault(t => t.Name == name);
                if (target == null)
                {
                    return false;
                }
                mutate(target);
                Write(members);
                return true;
            }
        }

        public bool Remove(string name)
        {
            lock (thisLock)
            {
                var members = GetMembers();
                int n = members.RemoveAll(t => t.Name == name);
                if (n == 0)
                {
                    return false;
                }
                Write(members);
                return true;
            }
        }

        // 呼叫端已持有 thisLock；先寫暫存檔再取代，被防毒等暫時鎖住時 SafeFile 會自動重試
        static void Write(List<Member> members)
        {
            SafeFile.WriteAllTextAtomic(Helper.GetRelativePath("data", "userData", "users.json"), JsonHelper.Serialize(members, indented: true));
        }
    }
}
