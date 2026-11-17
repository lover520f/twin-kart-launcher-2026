#include "../native/NativeSteamInventory.h"
#include <cstdio>

using namespace TwinKartSteamInventory;

namespace {
TwinKartEquipment::String Text(const wchar_t* data) {
    int size=static_cast<int>(wcslen(data))+1;
    return {data,size,size};
}
struct Part {TwinKartEquipment::String slot,id;unsigned long long links=0;};
static_assert(sizeof(Part)==0x28);
struct GearRow {TwinKartEquipment::String name;unsigned long long attributes=0;Array values;};
static_assert(sizeof(GearRow)==0x28);

void SparseParts(BYTE* map,const Part* storage,int count,unsigned allocated,int free) {
    *reinterpret_cast<Array*>(map)={reinterpret_cast<const BYTE*>(storage),count,count};
    *reinterpret_cast<unsigned*>(map+0x10)=allocated;
    *reinterpret_cast<int*>(map+0x28)=count;
    *reinterpret_cast<int*>(map+0x2c)=128;
    *reinterpret_cast<int*>(map+0x34)=free;
}
int Registration(const std::wstring& id,void*) {return id!=L"Kart:9" && id!=L"Motion:1";}
}

int main() {
    // An actual inventory row starts with LastUpdateDate, not its item array.
    // Reading +8 as an array used to treat this valid date as item storage.
    alignas(8) BYTE inventoryRow[0x28]{},itemRows[2*0x98]{};
    *reinterpret_cast<TwinKartEquipment::String*>(inventoryRow+8)=Text(L"2024-01-01");
    *reinterpret_cast<TwinKartEquipment::String*>(itemRows+8)=Text(L"Kart:1");
    *reinterpret_cast<TwinKartEquipment::String*>(itemRows+0x98+8)=Text(L"Character:1");
    auto& inventoryArray=*reinterpret_cast<Array*>(inventoryRow+0x18);
    inventoryArray={itemRows,2,2};
    Id observedItems[2]{};Snapshot inventoryCapture{observedItems,nullptr};
    if(!ReadItems(inventoryRow,inventoryCapture) || inventoryCapture.itemCount!=2 ||
       strcmp(observedItems[0].text,"Kart:1") || strcmp(observedItems[1].text,"Character:1"))return 9;
    inventoryArray.capacity=1;if(ReadItems(inventoryRow,inventoryCapture))return 10;
    inventoryArray.capacity=2;
    *reinterpret_cast<TwinKartEquipment::String*>(itemRows+0x98+8)=Text(L"malformed");
    if(ReadItems(inventoryRow,inventoryCapture) || inventoryCapture.itemCount!=1)return 11;

    alignas(8) BYTE map[0x50]{};
    const Part parts[]={{Text(L"FinishMotion"),Text(L"Motion:1")},{Text(L"Unknown"),Text(L"ignored")},
        {Text(L"Costume"),Text(L"Costume:1")}};
    SparseParts(map,parts,3,5,1);
    Outfit outfit{};outfit.character=true;
    if(!ReadParts(map,outfit) || strcmp(outfit.fields[1].text,"Costume:1") || strcmp(outfit.fields[2].text,"Motion:1"))return 1;
    const Part display[]={{Text(L"Costume"),Text(L"Costume:2")}};
    SparseParts(map,display,1,1,0);
    if(!ReadParts(map,outfit) || strcmp(outfit.fields[1].text,"Costume:1"))return 2;
    SparseParts(map,display,1,1,1);
    if(ReadParts(map,outfit))return 3;
    Id invalid;
    if(CopyId(Text(L"Kart:1:2"),invalid) || CopyId(Text(L"Kart:"),invalid) || CopyId(Text(L"Malformed"),invalid))return 4;

    Id items[27]{};Outfit outfits[17]{};
    Snapshot capture{items,outfits};
    for(int index=0;index<8;++index) {
        auto& rider=outfits[capture.outfitCount++];rider.character=true;
        sprintf_s(rider.fields[0].text,"Character:%d",index+1);
        sprintf_s(rider.fields[1].text,"Costume:%d",index+1);
        items[capture.itemCount++]=rider.fields[0];items[capture.itemCount++]=rider.fields[1];
    }
    for(int index=0;index<9;++index) {
        auto& kart=outfits[capture.outfitCount++];sprintf_s(kart.fields[0].text,"Kart:%d",index+1);
        items[capture.itemCount++]=kart.fields[0];
    }
    strcpy_s(outfits[0].fields[2].text,"Motion:1");
    strcpy_s(items[capture.itemCount++].text,"Motion:1");
    GearVariant level{};level.points=5;level.values[0]=1.0f;level.longSlide=1;
    GearRow rows[GearRules]{};
    for(auto& row:rows)row={Text(L"AutoLongSlideDrift"),0,{reinterpret_cast<const BYTE*>(&level),1,1}};
    if(!ReadGear({reinterpret_cast<const BYTE*>(rows),GearRules,GearRules},capture.gear) ||
        capture.gear[0].levels[0].points!=5 || capture.gear[0].levels[0].values[0]!=1.0f)return 5;
    level.longSlide=2;
    GearRule invalidGear[GearRules];
    if(ReadGear({reinterpret_cast<const BYTE*>(rows),GearRules,GearRules},invalidGear))return 6;

    Documents docs;
    constexpr char Sha[]="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    TwinKartRace::AiRoster roster;
    if(!Compose(capture,Sha,Registration,nullptr,docs) || docs.itemCount!=26 || docs.characters!=8 || docs.karts!=8 || docs.missingBodies!=1 ||
       docs.items.find("X Kart:9\n")==std::string::npos || docs.roster.find("C Character:1 Costume:1 - - - -\n")==std::string::npos ||
       docs.items.find("G AutoLongSlideDrift 5 3f800000") == std::string::npos ||
       !roster.Load(docs.roster.data(),docs.roster.size()))return 7;
    items[capture.itemCount++]=items[0];
    if(Compose(capture,Sha,Registration,nullptr,docs))return 8;
    puts("PASS Steam sparse-map bounds, native slot preference, registration and runtime catalog format.");
}
