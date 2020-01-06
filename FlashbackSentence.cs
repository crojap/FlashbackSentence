using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FlashbackSentence : MonoBehaviour
{
    //string _scentence = "Welcome to JoyCastle.Let's make an awesome game together.Welcome to JoyCastle.";
    string _scentence = "Focused, hard work is the real key to success. Keep your eyes on the goal, and just"+
    "keep taking the next step towards completing it.";

    void Start()
    {
        SplitSentenceFun(_scentence);
        Debug.Log("反转后的英文句子是：" + flashbackSentenceResult);
    }

    int index = 0;
    int endIndex = 0;
    string flashbackSentenceResult = string.Empty;
    void SplitSentenceFun(string sentence) {
        int commaIndex = 0;
        int fullstopIndex = 0;
        index = 0;
        fullstopIndex = sentence.IndexOf('.');
        if (sentence.Contains(","))
        {
            commaIndex = sentence.IndexOf(',');
            if (commaIndex > fullstopIndex)
                endIndex = fullstopIndex;
            else
                endIndex = commaIndex;
        }
        else
            endIndex = fullstopIndex;

        string splitSentence = sentence.Substring(index, endIndex);
        flashbackSentenceResult += (FlashbackSentenceFun(splitSentence) + (commaIndex == 0?". ":", "));
        index = endIndex+1;
        if(index < sentence.Length - 1)
            SplitSentenceFun(sentence.Substring(index));
    }

    string FlashbackSentenceFun(string sentence) {
        string[] arr = sentence.Split(' ');
        string result = string.Empty;
        for (int i = arr.Length - 1; i >= 0; i--)
        {
            result += arr[i] + ((i == 0 || i == 1) ? "" : " ") ;
        }
        return result;
    }
}
//以上还可以使用stack的方式处理，拆分句子为单词添加进stack，根据stack先进后出进行处理
// 使用100个大小相同，彼此不重叠的正方形尽可能填满一个m*n的矩形区域，计算正方形边长
//mn/100能够得到正方形面积，根据面积可以大概推算出正方形的边长