#pragma once

#include "NativeAiAssets.h"
#include "NativeAiRoster.h"
#include <map>
#include <set>
#include <string>
#include <cmath>
#include <vector>
#include <istream>

namespace TwinKartSteamInventory {
// Exact Steam build layout used by this client's UDataTable::GetRowMap and
// table 0x19's parts lookup. The caller pins the executable before loading it.
constexpr int MaxRows=10000,MaxOutfits=4096;
struct Id {char text[97]{};};
struct Outfit {Id fields[10];bool character=false;};
struct GearVariant {int points;float values[12];BYTE longSlide,reserved[3];};
static_assert(sizeof(GearVariant)==0x38);
constexpr int GearRules=9,MaxGearLevels=10;
struct GearRule {char name[48]{};int count=0;GearVariant levels[MaxGearLevels]{};};
struct Snapshot {Id* items;Outfit* outfits;int itemCount=0,outfitCount=0;GearRule gear[GearRules];int stage=0,rowStage=0;wchar_t rowName[96]{};const std::vector<Id>* baseline=nullptr;};
struct Array {const BYTE* data;int count,capacity;};
using ReadName=bool(*)(unsigned long long,wchar_t*,size_t);
using IsLive=bool(*)(const BYTE*);

template<class Visitor> bool VisitMap(const BYTE* map,int stride,int maximum,Visitor visit) {
    const auto& array=*reinterpret_cast<const Array*>(map);
    int bits=*reinterpret_cast<const int*>(map+0x28),bitCapacity=*reinterpret_cast<const int*>(map+0x2c);
    int free=*reinterpret_cast<const int*>(map+0x34);
    if(array.count<0 || array.count>maximum || array.capacity<array.count || array.capacity>maximum*2 ||
       bits!=array.count || bitCapacity<bits || bitCapacity>maximum*4+128 || free<0 || free>bits || (bits && !array.data))return false;
    auto words=*reinterpret_cast<const unsigned* const*>(map+0x20);
    if(!words){if(bits>128)return false;words=reinterpret_cast<const unsigned*>(map+0x10);}
    int allocated=0;
    for(int index=0;index<bits;++index)if(words[index/32]&(1u<<(index%32))) {
        if(!visit(array.data+static_cast<size_t>(index)*stride))return false;
        ++allocated;
    }
    return allocated==bits-free;
}

inline bool CopyId(const TwinKartEquipment::String& value,Id& id) {
    if(!TwinKartEquipment::Valid(value,97) || !TwinKartEquipment::Nonempty(value))return false;
    bool colon=false;
    for(int index=0;index<value.count-1;++index) {
        wchar_t ch=value.data[index];
        bool alpha=(ch>=L'A'&&ch<=L'Z') || (ch>=L'a'&&ch<=L'z');
        if(ch==L':' && !colon && index>0 && value.count-index-2>=1 && value.count-index-2<=18)colon=true;
        else if(colon ? (ch<L'0'||ch>L'9') : (!alpha && (index==0 || !(ch==L'_' || (ch>=L'0'&&ch<=L'9')))))return false;
        id.text[index]=static_cast<char>(ch);
    }
    id.text[value.count-1]=0;
    return colon;
}

inline bool ReadBaselineItems(std::istream& input,const char* fingerprint,std::vector<Id>& result) {
    std::string line;if(!std::getline(input,line) || line!=std::string("TKITEMS1 ")+fingerprint)return false;
    std::vector<Id> pending;std::set<std::string> seen;
    while(std::getline(input,line)) {
        if(line.empty() || line.size()>96 || pending.size()>=MaxRows || !seen.insert(line).second)return false;
        std::wstring text(line.begin(),line.end());Id id;
        if(!CopyId({text.c_str(),static_cast<int>(text.size()+1),static_cast<int>(text.size()+1)},id))return false;
        pending.push_back(id);
    }
    if(!input.eof() || pending.empty())return false;
    result=std::move(pending);return true;
}
inline bool AppendItem(Snapshot& capture,const Id& item) {
    for(int index=0;index<capture.itemCount;++index)if(!strcmp(capture.items[index].text,item.text))return true;
    if(capture.itemCount>=MaxRows)return false;
    capture.items[capture.itemCount++]=item;return true;
}

inline const BYTE* StockRow(BYTE* image,const wchar_t* objectPath,const wchar_t* rowType,ReadName read,IsLive live,int& stage,wchar_t (&name)[96]) {
    TwinKartRace::ObjectiveSoftReference reference{};
    using Name=void*(__fastcall*)(void*,const wchar_t*,int);
    using Load=BYTE*(__fastcall*)(TwinKartRace::ObjectiveSoftReference*);
    using GetClass=void*(__fastcall*)();
    stage=1;reinterpret_cast<Name>(image+0x2589130)(&reference.path.name,objectPath,1);
    if(!reference.path.name)return nullptr;
    stage=2;auto table=reinterpret_cast<Load>(image+0xa42300)(&reference);
    if(!live(table) || *reinterpret_cast<void**>(table+0x10)!=reinterpret_cast<GetClass>(image+0x4425db0)())return nullptr;
    stage=3;auto type=*reinterpret_cast<const BYTE**>(table+0x28);
    name[0]=0;
    if(!live(type))return nullptr;
    stage=31;if(!read(*reinterpret_cast<const unsigned long long*>(type+0x18),name,ARRAYSIZE(name)))return nullptr;
    stage=32;if(wcscmp(name,rowType))return nullptr;
    stage=4;auto method=(*reinterpret_cast<BYTE***>(table))[0x288/8];
    if(method<image || method>=image+0x4ed0000)return nullptr;
    stage=5;const BYTE* map=reinterpret_cast<const BYTE*(__fastcall*)(const BYTE*)>(method)(table);
    if(map!=table+0x30)return nullptr;
    stage=6;const BYTE* row=nullptr;
    bool single=VisitMap(map,0x18,128,[&](const BYTE* pair) {
        if(row)return false;
        row=*reinterpret_cast<const BYTE* const*>(pair+8);
        return row!=nullptr;
    });
    if(single && row)stage=7;
    return single?row:nullptr;
}

inline bool ReadParts(const BYTE* map,Outfit& outfit) {
    static constexpr const wchar_t* RiderSlots[]{L"Character",L"Costume",L"FinishMotion",L"RetireMotion",L"VictoryMotion",L"DefeatMotion"};
    const auto slots=outfit.character?RiderSlots:TwinKartEquipment::KartKeys;
    const unsigned size=outfit.character?ARRAYSIZE(RiderSlots):ARRAYSIZE(TwinKartEquipment::KartKeys);
    // Display entries fill holes. If the client defines both for a slot, its
    // default equipment entry is used for driving and AI creation.
    return VisitMap(map,0x28,64,[&](const BYTE* entry) {
        const auto& key=*reinterpret_cast<const TwinKartEquipment::String*>(entry);
        if(!TwinKartEquipment::Valid(key,97) || !TwinKartEquipment::Nonempty(key))return false;
        for(unsigned index=1;index<size;++index)if(!wcscmp(key.data,slots[index])) {
            const auto& value=*reinterpret_cast<const TwinKartEquipment::String*>(entry+0x10);
            return outfit.fields[index].text[0] || CopyId(value,outfit.fields[index]);
        }
        return true;
    });
}

inline bool ReadGear(const Array& array,GearRule (&gear)[GearRules]) {
    if(!array.data || array.count!=GearRules || array.capacity<array.count || array.capacity>32)return false;
    for(int index=0;index<array.count;++index) {
        const auto row=array.data+index*0x28;
        auto& rule=gear[index];
        const auto& name=*reinterpret_cast<const TwinKartEquipment::String*>(row);
        if(!TwinKartEquipment::Valid(name,ARRAYSIZE(rule.name)) || !TwinKartEquipment::Nonempty(name))return false;
        for(int letter=0;letter<name.count-1;++letter) {
            wchar_t ch=name.data[letter];
            if(!((ch>=L'A'&&ch<=L'Z') || (ch>=L'a'&&ch<=L'z')))return false;
            rule.name[letter]=static_cast<char>(ch);
        }
        rule.name[name.count-1]=0;
        const auto& variants=*reinterpret_cast<const Array*>(row+0x18);
        if(!variants.data || variants.count<1 || variants.count>MaxGearLevels || variants.capacity<variants.count || variants.capacity>32)return false;
        rule.count=variants.count;
        memcpy(rule.levels,variants.data,variants.count*sizeof(GearVariant));
        for(int level=0;level<rule.count;++level) {
            const auto& variant=rule.levels[level];
            if(variant.points<1 || variant.points>MaxGearLevels || variant.longSlide>1)return false;
            for(float value:variant.values)if(!std::isfinite(value))return false;
        }
    }
    return true;
}

inline bool ReadItems(const BYTE* row,Snapshot& capture) {
    // FKtClientInventoryDef is 0x28 bytes. LastUpdateDate is an FString at
    // +8; itemDefVector is the TArray at +0x18, not the first row member.
    const auto& array=*reinterpret_cast<const Array*>(row+0x18);
    capture.itemCount=0;
    if(!array.data || array.count<1 || array.count>MaxRows || array.capacity<array.count || array.capacity>2*MaxRows)return false;
    for(int index=0;index<array.count;++index) {
        const auto& text=*reinterpret_cast<const TwinKartEquipment::String*>(array.data+index*0x98+8);
        if(!CopyId(text,capture.items[index]))return false;
        ++capture.itemCount;
    }
    return true;
}

inline bool ReadTables(BYTE* image,Snapshot& capture,ReadName read,IsLive live) {
    capture.itemCount=0;capture.outfitCount=0;capture.stage=1;capture.rowStage=0;
    __try {
        // KtItemTable is the package name. Its actual cooked RowStruct is
        // FKtClientInventoryDef, independently observed in the owned client.
        const BYTE* items=StockRow(image,L"/Game/DataTable/Def/KtItemTable.KtItemTable",L"KtClientInventoryDef",read,live,capture.rowStage,capture.rowName);
        if(!items)return false;
        capture.stage=2;if(!ReadItems(items,capture))return false;
        capture.stage=3;if(capture.baseline)for(const auto& item:*capture.baseline)if(!AppendItem(capture,item))return false;
        capture.stage=4;const BYTE* parts=StockRow(image,L"/Game/DataTable/Def/KtItemPartsSetDef.KtItemPartsSetDef",L"KtClientItemPartsSetDef",read,live,capture.rowStage,capture.rowName);
        if(!parts)return false;
        capture.stage=5;if(!VisitMap(parts+8,0xb8,MaxOutfits,[&](const BYTE* entry) {
            Id key;
            if(!CopyId(*reinterpret_cast<const TwinKartEquipment::String*>(entry),key))return false;
            bool character=!strncmp(key.text,"Character:",10);
            if(!character && strncmp(key.text,"Kart:",5))return true;
            auto& outfit=capture.outfits[capture.outfitCount++];outfit={};outfit.character=character;outfit.fields[0]=key;
            // FString map key occupies 0x10 bytes. Its value is the pair of
            // 0x50-byte maps in FKtItemPartsDef.
            return ReadParts(entry+0x10,outfit) && ReadParts(entry+0x60,outfit);
        }))return false;
        capture.stage=6;const BYTE* gear=StockRow(image,L"/Game/DataTable/Def/KtKartGearSetDef.KtKartGearSetDef",L"KtKartGearSetDef",read,live,capture.rowStage,capture.rowName);
        if(!gear)return false;
        capture.stage=7;if(!ReadGear(*reinterpret_cast<const Array*>(gear+8),capture.gear))return false;
        capture.stage=8;return true;
    } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}

struct Documents {std::string items,roster;int itemCount=0,characters=0,karts=0,missingBodies=0;};

inline bool Compose(const Snapshot& capture,const char* fingerprint,TwinKartRace::AiRoster::Available available,void* context,Documents& output) {
    std::set<std::string> known,seen;
    Documents result;
    result.items=std::string("TKSTEAM2 ")+fingerprint+'\n';result.roster="TKAI1\n";
    for(int index=0;index<capture.itemCount;++index) {
        const auto& item=capture.items[index].text;
        if(!known.insert(item).second)return false;
        result.items+="I ";result.items+=item;result.items+='\n';
    }
    result.itemCount=capture.itemCount;
    std::map<std::string,int> registrations;
    auto exists=[&](const char* id) {
        auto found=registrations.find(id);
        if(found!=registrations.end())return found->second;
        std::string text=id;
        return registrations.emplace(text,available(std::wstring(text.begin(),text.end()),context)).first->second;
    };
    for(int index=0;index<capture.outfitCount;++index) {
        const auto& outfit=capture.outfits[index];auto id=outfit.fields[0].text;
        if(!known.count(id))continue;
        if(!seen.insert(id).second)return false;
        int registered=exists(id);
        if(registered<0)return false;
        if(registered==0) {
            if(!outfit.character){result.items+="X ";result.items+=id;result.items+='\n';++result.missingBodies;}
            continue;
        }
        std::string row=outfit.character?"C ":"K ";row+=id;
        const unsigned count=outfit.character?6:10;
        bool costumeMissing=false;
        for(unsigned slot=1;slot<count;++slot) {
            auto part=outfit.fields[slot].text;
            int ready=part[0] && known.count(part)?exists(part):0;
            if(ready<0)return false;
            if(outfit.character && slot==1 && !ready)costumeMissing=true;
            row+=' ';row+=ready==1?part:"-";
        }
        if(costumeMissing)continue;
        row+='\n';result.items+=row;result.roster+=row;
        if(outfit.character)++result.characters;else ++result.karts;
    }
    if(result.characters<8 || result.karts<8)return false;
    for(const auto& rule:capture.gear)for(int level=0;level<rule.count;++level) {
        const auto& variant=rule.levels[level];
        result.items+="G "+std::string(rule.name)+' '+std::to_string(variant.points);
        // Float bit patterns preserve the exact values on both sides.
        for(float value:variant.values) {
            unsigned bits;memcpy(&bits,&value,sizeof(bits));
            char encoded[9];sprintf_s(encoded,"%08x",bits);
            result.items+=' ';result.items+=encoded;
        }
        result.items+=' ';result.items+=variant.longSlide?'1':'0';result.items+='\n';
    }
    result.items+="E "+std::to_string(result.itemCount)+' '+std::to_string(result.characters)+' '+std::to_string(result.karts)+' '+std::to_string(result.missingBodies)+'\n';
    output=std::move(result);return true;
}
}
