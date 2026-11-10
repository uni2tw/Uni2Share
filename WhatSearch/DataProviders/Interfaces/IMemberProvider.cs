using System.Collections.Generic;
using WhatSearch.Models;

namespace WhatSearch.DataProviders.Interfaces
{
    public interface IMemberProvider
    {
        List<Member> GetMembers();
        void SaveMember(Member mem);
        /// <summary>在鎖裡重新讀取、修改指定會員、再原子寫回（不會用過期的資料蓋掉別人剛改的欄位）；找不到會員回傳 false</summary>
        bool Update(string name, System.Action<Member> mutate);
        Member GetMember(string name);
        /// <summary>刪除會員；找不到回傳 false</summary>
        bool Remove(string name);
        Member GetMemberByToken(string accessToken);
    }
}
