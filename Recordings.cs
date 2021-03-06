using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace StreamCapture
{
    public class Recordings
    {
        private IConfiguration configuration;
        //Master dictionary of all shows we're interested in recording.  This is *not* the shows we necessarily will queue.
        //For example, this master list will have entires too far in the future, too many concurrent, etc.
        //The list is cleaned up when an entry is already completed.
        private Dictionary<string, RecordInfo> recordDict;
        //List of queued shows in datetime order
        //This list is derived from the recordDict master dictionary.  However, it's put in datetime order, and entries
        //too far in the future, or too many at once are omitted.  In other words, this is the list of shows
        //we'll actually queue to record/capture
        private List<RecordInfo> queuedRecordings;
        //This object holds the full schedule from live247.  It normally only has a day or two of data in it.
        //But we'll grab whatever they post.
        private Schedule schedule;


        public Recordings(IConfiguration _configuration)
        {
            recordDict = new Dictionary<string, RecordInfo>();
            configuration = _configuration;
            schedule = new Schedule();
        }
        
        //
        // This is a key member function.  This and 'CaptureStream' do the bulk of the work
        //
        // As such, I'll document this a bit more than usual so when I forget (tomorrow) I can read and remember some...
        //
        public List<RecordInfo> BuildRecordSchedule()
        {
            //Refresh keywords
            //
            //Reads keywords from the keywords.json file.  This is data used to determine which entries in the schedule
            //we are interested in recording/capturing
            Keywords keywords = new Keywords(configuration);

            //Refresh schedule from website
            //
            // This goes and grabs the online .json file which is the current schedule from Live247
            // This list is *all* the shows currently posted.  Usually, live247 only posts a day or two at a time.
            schedule.LoadSchedule(configuration["debug"]).Wait();
            List<ScheduleShow> scheduleShowList = schedule.GetScheduledShows();

            //Go through the shows and load up recordings if there's a match
            //
            //This loop compares keywords and schedule entires, when there's a match, it 
            //adds creates a RecordInfo istance and adds it to a master Dictionary
            //of shows we thing we care about.  Later we'll determine which ones we'll actually capture. 
            foreach(ScheduleShow scheduleShow in scheduleShowList)
            {
                //Find any shows that match
                Tuple<KeywordInfo,int> tuple = keywords.FindMatch(scheduleShow.name);   
                if (tuple != null)
                {
                    KeywordInfo keywordInfo = tuple.Item1; 

                    //Build record info if already exists, otherwise, create new                 
                    RecordInfo recordInfo=GetRecordInfo(BuildRecordInfoKeyValue(scheduleShow));

                    //Fill out the recording info
                    recordInfo.channels.AddUpdateChannel(scheduleShow.channel, scheduleShow.quality, scheduleShow.language);
                    recordInfo.id = scheduleShow.id;
                    recordInfo.description = scheduleShow.name;
                    recordInfo.strStartDT = scheduleShow.time;
                    //recordInfo.strStartDT = DateTime.Now.AddHours(4).ToString();
                    recordInfo.strEndDT = scheduleShow.end_time;
                    recordInfo.strDuration = scheduleShow.runtime;
                    //recordInfo.strDuration = "1";
                    recordInfo.strDTOffset = configuration["schedTimeOffset"];
                    recordInfo.preMinutes = keywordInfo.preMinutes;
                    recordInfo.postMinutes = keywordInfo.postMinutes;
                    recordInfo.starredFlag = keywordInfo.starredFlag;
                    recordInfo.emailFlag = keywordInfo.emailFlag;
                    recordInfo.qualityPref = keywordInfo.qualityPref;
                    recordInfo.categoryPref = keywordInfo.categoryPref;
                    recordInfo.langPref = keywordInfo.langPref;
                    recordInfo.channelPref = keywordInfo.channelPref;
                    recordInfo.category = scheduleShow.category;

                    recordInfo.keywordPos = tuple.Item2;  //used for sorting the most important shows 

                    //Clean up description, and then use as filename
                    recordInfo.fileName = scheduleShow.name.Replace(' ','_');
                    string myChars = @"|'/\ ,<>#@!+&^*()~`;";
                    string invalidChars = myChars + new string(Path.GetInvalidFileNameChars());
                    foreach (char c in invalidChars)
                    {
                        recordInfo.fileName = recordInfo.fileName.Replace(c.ToString(), "");
                    }

                    //If starred, add designator to filename
                    if(recordInfo.starredFlag)
                        recordInfo.fileName = "+_" + recordInfo.fileName;

                    //Update or add
                    AddUpdateRecordInfo(BuildRecordInfoKeyValue(recordInfo),recordInfo);
                }
            }

            //Return shows that should actually be queued (omitted those already done, too far in the future, etc...)
            //
            //This is an important call.  Please see remarks in this member function for more info.
            return GetShowsToQueue();
        }

        public RecordInfo GetRecordInfo(string recordInfoKey)
        {
            RecordInfo recordInfo=null;
            bool recFoundFlag=recordDict.TryGetValue(recordInfoKey,out recordInfo);

            //Add new if not found
            if(!recFoundFlag)
                recordInfo=new RecordInfo();

            return recordInfo;
        }
        private string BuildRecordInfoKeyValue(RecordInfo recordInfo)        
        {
            return recordInfo.strStartDT + recordInfo.description;
        }

        private string BuildRecordInfoKeyValue(ScheduleShow scheduleShow)        
        {
            return scheduleShow.time + scheduleShow.name;
        }

        private void AddUpdateRecordInfo(string recordInfoKey,RecordInfo recordInfo)
        {
            if(recordDict.ContainsKey(recordInfoKey))
                recordDict[recordInfoKey]=recordInfo;
            else
                recordDict.Add(recordInfoKey,recordInfo);
        }

        private void DeleteRecordInfo(RecordInfo recordInfoToDelete)
        {
            recordDict.Remove(BuildRecordInfoKeyValue(recordInfoToDelete));
        }

        //Figures out which schedule entries we actually intend to queue for capture
        //
        //It uses keyword order and maximum concurrent captures allowed to determine which
        //entires are queued and which are passed over.
        private List<RecordInfo> GetShowsToQueue()
        {
            //Build mail to send out
            Mailer mailer = new Mailer();
            string concurrentShowText = "";
            string currentScheduleText="";

            //Starting new as this is always time dependent
            queuedRecordings=new List<RecordInfo>();

            //Go through potential shows and add the ones we should record
            //Omit those which are already done, too far in the future, or too many concurrent.  (already queued is fine obviously)
            //
            //recordingList has the shows in order of the keywords which they matched on
            List<RecordInfo> recordingList = SortBasedOnKeywordPos(recordDict.Values.ToList());
            foreach(RecordInfo recordInfo in recordingList.ToArray())
            {
                bool showAlreadyDone=recordInfo.GetEndDT()<DateTime.Now;
                bool showTooFarAway=recordInfo.GetStartDT()>DateTime.Now.AddHours(Convert.ToInt32(configuration["hoursInFuture"]));
                bool tooManyConcurrent=!IsConcurrencyOk(recordInfo,queuedRecordings);

                if(showAlreadyDone)
                {
                    Console.WriteLine($"{DateTime.Now}: Show already finished: {recordInfo.description} at {recordInfo.GetStartDT()}");
                    DeleteRecordInfo(recordInfo);
                }
                else if(showTooFarAway)
                    Console.WriteLine($"{DateTime.Now}: Show too far away: {recordInfo.description} at {recordInfo.GetStartDT()}");
                else if(recordInfo.processSpawnedFlag)
                    Console.WriteLine($"{DateTime.Now}: Show already queued: {recordInfo.description} at {recordInfo.GetStartDT()}");    
                else if(tooManyConcurrent)
                {
                    Console.WriteLine($"{DateTime.Now}: Too many at once: {recordInfo.description} at {recordInfo.GetStartDT()} - {recordInfo.GetEndDT()}"); 
                    concurrentShowText=mailer.AddConcurrentShowToString(concurrentShowText,recordInfo);  
                }
                
                //Let's queue this since it looks good
                if(!showAlreadyDone && !showTooFarAway && !tooManyConcurrent)
                {
                    queuedRecordings = AddToSortedList(recordInfo,queuedRecordings);
                }
            }
             
            //build email and print schedule
            Console.WriteLine($"{DateTime.Now}: Current Schedule ==================");
            foreach(RecordInfo recordInfo in queuedRecordings)
            {
                Console.WriteLine($"{DateTime.Now}: {recordInfo.description} at {recordInfo.GetStartDT()} - {recordInfo.GetEndDT()}");
                currentScheduleText=mailer.AddCurrentScheduleToString(currentScheduleText,recordInfo);  
            }
            Console.WriteLine($"{DateTime.Now}: ===================================");

            //Send mail if we have something
            string mailText="";
            if(!string.IsNullOrEmpty(currentScheduleText))
                mailText=mailText+currentScheduleText;   
            if(!string.IsNullOrEmpty(concurrentShowText))
                mailText=mailText+concurrentShowText;                 
            if(!string.IsNullOrEmpty(mailText))
                mailer.SendNewShowMail(configuration,mailText);                   

            //Ok, we can now return the list
            return queuedRecordings;
        }

        private List<RecordInfo> AddToSortedList(RecordInfo recordInfoToAdd,List<RecordInfo> list)
        {
            //Add to a sorted list based on start time
            RecordInfo[] recordInfoArray = list.ToArray();
            for(int idx=0;idx<recordInfoArray.Length;idx++)
            {
                if(recordInfoToAdd.GetStartDT()<recordInfoArray[idx].GetStartDT())
                {
                    list.Insert(idx,recordInfoToAdd);
                    return list;
                }
            }

            //If we've made it this far, then add to the end
            list.Add(recordInfoToAdd);
            return list;
        }

        //Checks to make sure we're not recording too many shows at once
        //
        //The approach taken is to try and add shows to this queued list one at a time *in keyword order*.
        //This way, shows matched on higher keywords, get higher priority.
        private bool IsConcurrencyOk(RecordInfo recordingToAdd,List<RecordInfo> recordingList)
        {
            //Temp list to test with
            List<RecordInfo> tempList = new List<RecordInfo>(recordingList);
            bool okToAddFlag=true;

            //Add to this temp list and then we'll check for concurrency
            tempList=AddToSortedList(recordingToAdd,tempList);

            //stack to keep track of end dates
            List<DateTime> endTimeStack = new List<DateTime>();

            int maxConcurrent=Convert.ToInt16(configuration["concurrentCaptures"]);
            int concurrent=0;

            RecordInfo[] recordInfoArray = tempList.ToArray();
            for(int idx=0;idx<recordInfoArray.Length;idx++)
            {
                concurrent++;  //increment because it's a new record              

                //Check if we can decrement
                DateTime[] endTimeArray = endTimeStack.ToArray();
                for(int i=0;i<endTimeArray.Length;i++)
                {
                    if(recordInfoArray[idx].GetStartDT()>=endTimeArray[i])
                    {
                        concurrent--;
                        endTimeStack.Remove(endTimeArray[i]);
                    }
                }
                endTimeStack.Add(recordInfoArray[idx].GetEndDT());

                //Let's make sure we're not over max
                if(concurrent>maxConcurrent)
                {
                    okToAddFlag=false;
                }
            } 

            return okToAddFlag;         
        }

        //Shorts the items based on where they were found in the keyword list
        //This enables us to try and add shows in keyword priority (assuming some won't get a slot)
        private List<RecordInfo> SortBasedOnKeywordPos(List<RecordInfo> listToBeSorted)
        {
            List<RecordInfo> sortedList=new List<RecordInfo>();

            foreach(RecordInfo recordInfo in listToBeSorted)
            {
                bool insertedFlag=false;
                RecordInfo[] sortedArray = sortedList.ToArray();
                for(int idx=0;idx<sortedArray.Length;idx++)
                {
                    if(recordInfo.keywordPos<=sortedArray[idx].keywordPos)
                    {
                        sortedList.Insert(idx,recordInfo);
                        insertedFlag=true;
                        break;
                    }
                }

                //Not found, so add to the end
                if(!insertedFlag)
                {
                    sortedList.Add(recordInfo);
                }
            }

            return sortedList;
        }
    }
}