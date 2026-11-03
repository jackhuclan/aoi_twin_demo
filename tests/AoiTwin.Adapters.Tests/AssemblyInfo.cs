using Xunit;

// 适配器测试涉及多线程泵送与真实计时（退避、轮询节拍、全链路灌数据），
// 并行跑多个集会在 CI 双核 runner 上互相挤压造成假失败；串行执行代价可忽略。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
