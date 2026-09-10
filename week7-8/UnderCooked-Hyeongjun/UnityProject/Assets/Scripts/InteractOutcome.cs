// Interact 한 번의 결과 묶음.
// KitchenEnv.TryInteract가 돌려주고, ChefAgent가 보상 계산에 쓴다.
public struct InteractOutcome
{
    public InteractResult Result;

    // 상호작용 후 손에 남는 것.
    public ItemType NewHeldItem;

    // 카운터에서 '동료가 놓은' 물건을 집었을 때 그 동료의 인덱스. 그 외에는 -1.
    // 전달 보상(+0.15)을 놓은 쪽에도 소급해서 주기 위해 필요하다.
    public int TransferPartnerIndex;
}
